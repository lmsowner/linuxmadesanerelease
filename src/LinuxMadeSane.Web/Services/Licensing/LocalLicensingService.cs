// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Licensing;
using LinuxMadeSane.Infrastructure.Licensing;
namespace LinuxMadeSane.Web.Services.Licensing;

public sealed class LocalLicensingService(SqliteLicensingStore store, EntitlementVerifier verifier, LicensingClock? clock = null)
{
    private DateTimeOffset Now => clock?.Read().ConservativeUtc ?? DateTimeOffset.UtcNow;
    public static ReleaseLicence ApplicableRelease { get; } = ReadRelease();
    public static ReleaseLicence DraftRelease => ApplicableRelease with { LegalRegime = "lms_source_available_v1", TermsVersion = LicensingPolicy.Version, Approved = false, TermsHash = DraftTermsHash, Release = "internal-future-policy-preview", FirstPublicationUtc = null, ChangeDate = null, ChangeLicense = null };
    public static string DraftTermsHash { get; } = ReadTermsHash();
    private static ReleaseLicence ReadRelease()
    {
        using var stream = typeof(LocalLicensingService).Assembly.GetManifestResourceStream("Lms.Licensing.Release")!;
        return JsonSerializer.Deserialize<ReleaseLicence>(stream)!;
    }
    private static string ReadTermsHash()
    {
        using var stream = typeof(LocalLicensingService).Assembly.GetManifestResourceStream("Lms.About.LICENSE")
            ?? typeof(LocalLicensingService).Assembly.GetManifestResourceStream("Lms.About.LICENSE.md")
            ?? throw new InvalidOperationException("Bundled licence missing.");
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public Task<(LicensingState State, long Revision)> ReadAsync() => store.ReadAsync();
    public static string ApplicableTerms => AboutCredits.Notices.Single(n => n.FileName == "LICENSE").Text;

    public static bool HasAccepted(LicensingState state, string actor) => state.Acceptances.Any(a =>
        a.Actor == actor && a.TermsHash == ApplicableRelease.TermsHash && a.TermsVersion == ApplicableRelease.TermsVersion);

    public async Task AcceptTermsAsync(string actor, string termsHash, bool agreed)
    {
        // Bind explicit agreement to the bundled, approved release, never a browser-supplied policy.
        if (!agreed || string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || !ApplicableRelease.Approved ||
            termsHash != ApplicableRelease.TermsHash)
            throw new ArgumentException("Explicit agreement to the applicable release terms is required.");
        using var stream = typeof(LocalLicensingService).Assembly.GetManifestResourceStream(
            "Lms.About.LICENSE")
            ?? typeof(LocalLicensingService).Assembly.GetManifestResourceStream("Lms.About.LICENSE.md")
            ?? throw new InvalidOperationException("Bundled licence missing.");
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != termsHash)
            throw new InvalidOperationException("The bundled licence does not match this release. Agreement was not recorded.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (state, revision) = await store.ReadAsync();
            if (HasAccepted(state, actor)) return;
            var now = Now;
            state.Acceptances.Add(new(actor, ApplicableRelease.TermsVersion, termsHash, now));
            state.Audit.Add(new(Guid.NewGuid(), "terms_accepted", actor, now, $"Explicit acceptance of {ApplicableRelease.TermsVersion}; SHA-256 {termsHash}. Not a purchase or trial start."));
            try { await store.SaveAsync(state, revision); return; }
            catch (InvalidOperationException) when (attempt < 2) { }
        }
    }
    public async Task<LicensingStatus> StatusAsync(bool preview = false)
    {
        var (state, revision) = await store.ReadAsync();
        var storedLastSeen = state.LastSeenUtc;
        var now = Now;
        Entitlement? entitlement = null;
        if (state.Licence is not null && state.Organisation is not null)
        {
            try { entitlement = verifier.Verify(state.Licence, state.Organisation.Id); }
            catch (ArgumentException) { state = state with { InvalidImport = true }; }
        }
        var signal = clock?.Read();
        if (signal is not null && (state.LastSeenUtc is null || signal.ConservativeUtc > state.LastSeenUtc)) state = state with { LastSeenUtc = signal.ConservativeUtc };
        var result = LicensingPolicy.Derive(preview ? DraftRelease : ApplicableRelease, state, signal?.WallUtc ?? now, entitlement) with
        { PaidExpiresAtUtc = entitlement?.ExpiresAtUtc, DeclarationReviewDueAtUtc = state.Declarations.LastOrDefault()?.ReviewDueAtUtc };
        // Persist the clock signal, never regress it; no management dependency on these diagnostics.
        if (storedLastSeen is null || now > storedLastSeen.Value.AddMinutes(5))
        {
            try { await store.SaveAsync(state with { LastSeenUtc = state.LastSeenUtc > now ? state.LastSeenUtc : now }, revision); }
            catch (InvalidOperationException) { /* A concurrent authorised update wins. */ }
        }
        return result;
    }
    public async Task SelectUsageTypeAsync(string category, string actor, long revision)
    {
        if (category is not ("personal" or "education" or "non_profit" or "internal_business" or "commercial_services"))
            throw new ArgumentException("Choose a user type.");
        var (state, actual) = await store.ReadAsync(); CheckRevision(actual, revision);
        var org = state.Organisation;
        var id = org?.Id ?? Guid.NewGuid();
        var prior = state.Declarations.LastOrDefault();
        var revenue = category == "internal_business"
            ? prior?.Category == category ? prior.RevenueBand : "unknown" : "not_applicable";
        await DeclareAsync(new(id, org?.Name ?? "LMS installation", org?.GroupIdentity ?? id.ToString("N"),
            category, revenue, category == "commercial_services", true,
            CorrectionReason: "User type selected; not a terms acceptance, purchase or evaluation start."), actor, revision,
            startEvaluationOnTransition: false);
    }

    public async Task DeclareAsync(DeclarationInput input, string actor, long revision, bool startEvaluationOnTransition = true)
    {
        LicensingPolicy.Validate(input);
        var (state, actual) = await store.ReadAsync(); CheckRevision(actual, revision);
        if (state.Organisation is not null && state.Organisation.Id != input.OrganisationId) throw new ArgumentException("Import/transfer the existing organisation; changing IDs cannot reset evaluation.");
        if (state.Declarations.Count > 0 && (string.IsNullOrWhiteSpace(input.CorrectionReason) || input.CorrectionReason.Length > 500)) throw new ArgumentException("An auditable correction reason is required.");
        var wasCommunity = state.Declarations.LastOrDefault() is { } prior && LicensingPolicy.CommunityEligible(prior);
        var now = Now;
        var declaration = new UsageDeclaration(Guid.NewGuid(), input.OrganisationId, input.Category, input.RevenueBand,
            input.ThirdPartyCommercialUse || input.Category == "commercial_services", actor, now, now.AddYears(1), LicensingPolicy.Version,
            DraftTermsHash, input.FinancialYearEnd, input.ExchangeRateBasis);
        state.Declarations.Add(declaration);
        state.Audit.Add(new(Guid.NewGuid(), "declaration", actor, now, input.CorrectionReason ?? "Original authorised declaration; not a purchase or terms acceptance."));
        if (startEvaluationOnTransition && wasCommunity && !LicensingPolicy.CommunityEligible(declaration) && state.Evaluation is null)
        {
            state = state with { Evaluation = LicensingPolicy.Start(null, input.OrganisationId, now, "Community eligibility transition") };
            state.Audit.Add(new(Guid.NewGuid(), "evaluation_transition", actor, now, "First eligibility transition; original evaluation begins, no purchase or fee."));
        }
        await store.SaveAsync(state with { Organisation = new(input.OrganisationId, input.OrganisationName, input.GroupIdentity), LastSeenUtc = now }, actual);
    }
    public async Task StartAsync(string actor)
    {
        // Retry optimistic concurrency; all repeated starts retain exactly the original timestamp.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (state, revision) = await store.ReadAsync();
            if (state.Organisation is null) throw new ArgumentException("Declare the organisation first.");
            if (state.Evaluation is not null) return;
            if (state.Declarations.LastOrDefault() is not { } declaration || LicensingPolicy.CommunityEligible(declaration))
                throw new ArgumentException("Qualifying Community use does not consume an evaluation.");
            var now = Now;
            state.Audit.Add(new(Guid.NewGuid(), "evaluation_start", actor, now, "Original organisation-wide evaluation."));
            try { await store.SaveAsync(state with { Evaluation = LicensingPolicy.Start(null, state.Organisation.Id, now, actor), LastSeenUtc = now }, revision); return; }
            catch (InvalidOperationException) when (attempt < 2) { }
        }
    }
    public async Task ImportEvaluationAsync(Evaluation imported, string actor, string reason, long revision)
    {
        var (state, actual) = await store.ReadAsync(); CheckRevision(actual, revision);
        var now = Now;
        if (imported is null || imported.OrganisationId != state.Organisation?.Id || imported.StartedAtUtc > now || imported.StartedAtUtc < DateTimeOffset.UnixEpoch ||
            string.IsNullOrWhiteSpace(imported.Provenance) || imported.Provenance.Length > 200 || string.IsNullOrWhiteSpace(reason) || reason.Length > 500 ||
            state.Evaluation is not null && imported.StartedAtUtc > state.Evaluation.StartedAtUtc)
            throw new ArgumentException("An honour-based shared record cannot reset or extend an evaluation.");
        state.Audit.Add(new(Guid.NewGuid(), "evaluation_import", actor, now, reason));
        await store.SaveAsync(state with { Evaluation = imported, LastSeenUtc = now }, actual);
    }
    public async Task ImportAsync(SignedEntitlement envelope, string actor, long revision)
    {
        var (state, actual) = await store.ReadAsync(); CheckRevision(actual, revision);
        if (state.Organisation is null) throw new ArgumentException("Declare the organisation first.");
        Entitlement imported;
        try { imported = verifier.Verify(envelope, state.Organisation.Id); }
        catch (ArgumentException)
        {
            state.Audit.Add(new(Guid.NewGuid(), "invalid_import", actor, Now, "Signature or payload rejected; previous licence retained."));
            await store.SaveAsync(state with { InvalidImport = true }, actual); throw;
        }
        if (imported.NotBeforeUtc > Now || imported.ExpiresAtUtc <= Now)
            throw new ArgumentException("Licence is not currently valid; previous licence retained.");
        if (state.Licence is not null)
        {
            Entitlement? prior = null;
            try { prior = verifier.Verify(state.Licence, state.Organisation.Id); } catch (ArgumentException) { /* Replaced/rotated invalid key must not prevent a new valid licence. */ }
            if (prior is not null)
            {
            if (prior.EntitlementId == imported.EntitlementId) return;
            // Deterministic latest issuance, then ID; capacities never sum.
            if (prior.IssuedAtUtc > imported.IssuedAtUtc || prior.IssuedAtUtc == imported.IssuedAtUtc && prior.EntitlementId.CompareTo(imported.EntitlementId) >= 0)
                throw new ArgumentException("The retained entitlement takes precedence.");
            }
        }
        state.Audit.Add(new(Guid.NewGuid(), "entitlement_import", actor, Now, imported.EntitlementId.ToString()));
        await store.SaveAsync(state with { Licence = envelope, InvalidImport = false }, actual);
    }
    public async Task ImportOrganisationAsync(OrganisationTransfer transfer, string actor, string reason, long expectedRevision)
    {
        var (state, revision) = await store.ReadAsync(); CheckRevision(revision, expectedRevision);
        if (transfer is null || transfer.SchemaVersion != 1 || transfer.Organisation is null || transfer.Organisation.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(transfer.Organisation.Name) || string.IsNullOrWhiteSpace(transfer.Organisation.GroupIdentity) ||
            transfer.Organisation.Name.Length > 200 || transfer.Organisation.GroupIdentity.Length > 200 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || transfer.Declarations is null || transfer.Declarations.Length > 100 ||
            state.Organisation is not null && state.Organisation.Id != transfer.Organisation.Id)
            throw new ArgumentException("A matching organisation transfer and auditable reason are required.");
        foreach (var declaration in transfer.Declarations)
        {
            if (declaration is null || declaration.OrganisationId != transfer.Organisation.Id || declaration.DeclaredAtUtc > Now || declaration.Id == Guid.Empty)
                throw new ArgumentException("Invalid transferred declaration.");
            LicensingPolicy.Validate(new(transfer.Organisation.Id, transfer.Organisation.Name, transfer.Organisation.GroupIdentity,
                declaration.Category, declaration.RevenueBand, declaration.ThirdPartyCommercialUse, true));
        }
        if (transfer.Evaluation is { } evaluation && (evaluation.OrganisationId != transfer.Organisation.Id ||
            evaluation.StartedAtUtc > Now || evaluation.StartedAtUtc < DateTimeOffset.UnixEpoch || string.IsNullOrWhiteSpace(evaluation.Provenance)))
            throw new ArgumentException("Invalid original evaluation.");
        if (transfer.Licence is not null) verifier.Verify(transfer.Licence, transfer.Organisation.Id);
        var declarations = state.Declarations.Concat(transfer.Declarations).GroupBy(d => d.Id).Select(g => g.Distinct().Single()).OrderBy(d => d.DeclaredAtUtc).ToList();
        var original = new[] { state.Evaluation, transfer.Evaluation }.Where(e => e is not null).OrderBy(e => e!.StartedAtUtc).FirstOrDefault();
        state.Audit.Add(new(Guid.NewGuid(), "organisation_transfer", actor, Now, reason));
        await store.SaveAsync(state with { Organisation = state.Organisation ?? transfer.Organisation, Declarations = declarations,
            Evaluation = original, Licence = state.Licence ?? transfer.Licence }, revision);
    }
    public async Task ReconcileServerAsync(ServerIdentity server, string actor, long expectedRevision)
    {
        if (server is null || server.Id == Guid.Empty || string.IsNullOrWhiteSpace(server.ExternalIdentity) || server.ExternalIdentity.Length > 200 || server.Customer?.Length > 200) throw new ArgumentException("Stable organisation server identity required.");
        var (state, actual) = await store.ReadAsync(); CheckRevision(actual, expectedRevision);
        if (state.Organisation is null) throw new ArgumentException("Declare the organisation first.");
        var prior = state.Servers.FindIndex(s => s.ExternalIdentity == server.ExternalIdentity);
        if (prior >= 0) state.Servers[prior] = server with { Id = state.Servers[prior].Id }; else state.Servers.Add(server);
        state.Audit.Add(new(Guid.NewGuid(), "server_reconciliation", actor, Now, "Explicit enrolment/decommission; not a billing acceptance."));
        await store.SaveAsync(state, actual);
    }
    public async Task DismissAsync(string actor, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100) throw new ArgumentException("Invalid notice version.");
        var (state, revision) = await store.ReadAsync();
        if (state.Dismissals.Contains(new(actor, version))) return;
        state.Dismissals.Add(new(actor, version)); await store.SaveAsync(state, revision);
    }
    private static void CheckRevision(long actual, long expected)
    { if (actual != expected) throw new InvalidOperationException("Licensing state changed. Refresh and retry."); }
}
