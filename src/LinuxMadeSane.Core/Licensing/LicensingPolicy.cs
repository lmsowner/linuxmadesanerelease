// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Licensing;

public static class LicensingPolicy
{
    public const string Version = "lms-source-available-1.0";
    public const string Welcome = "LMS is free for personal and homelab use, educational institutions, qualifying non-profit organisations, and internal business use by organisations with consolidated annual gross revenue below US$1 million. Businesses at or above that threshold, and organisations using LMS to provide commercial services to third parties, require Professional. Eligible organisations can evaluate Professional with full functionality for 30 days.";
    public static bool CommunityEligible(UsageDeclaration declaration) => !declaration.ThirdPartyCommercialUse &&
        (declaration.Category is "personal" or "education" or "non_profit" ||
         declaration.Category == "internal_business" && declaration.RevenueBand == "below_usd_1m");
    public static void Validate(DeclarationInput input)
    {
        if (input is null || input.OrganisationId == Guid.Empty || string.IsNullOrWhiteSpace(input.OrganisationName) ||
            input.OrganisationName.Length > 200 || string.IsNullOrWhiteSpace(input.GroupIdentity) || input.GroupIdentity.Length > 200 || !input.Confirmed ||
            input.Category is not ("personal" or "education" or "non_profit" or "internal_business" or "commercial_services") ||
            input.RevenueBand is not ("below_usd_1m" or "at_or_above_usd_1m" or "not_applicable" or "unknown") ||
            input.Category == "internal_business" && input.RevenueBand == "not_applicable" ||
            input.CorrectionReason?.Length > 500 || input.FinancialYearEnd?.Length > 40 || input.ExchangeRateBasis?.Length > 1000)
            throw new ArgumentException("An authorised, complete organisation declaration is required.");
    }
    public static Evaluation Start(Evaluation? original, Guid organisation, DateTimeOffset now, string provenance) =>
        original is not null ? original.OrganisationId == organisation ? original : throw new ArgumentException("Organisation does not match the evaluation.") :
        new(organisation, now.ToUniversalTime(), provenance);
    public static LicensingStatus Derive(ReleaseLicence release, LicensingState state, DateTimeOffset now, Entitlement? entitlement = null)
    {
        var count = state.Servers.Where(s => s.Enrolled).Select(s => s.ExternalIdentity).Distinct(StringComparer.Ordinal).Count();
        var notices = new List<string>();
        var clockUncertain = state.LastSeenUtc > now.AddMinutes(5);
        if (clockUncertain) notices.Add("clock_uncertain");
        if (state.InvalidImport) notices.Add("invalid_import");
        // Applicable immutable release metadata takes precedence over current website policy.
        if (release.LegalRegime == "historical")
            return new("historical", "historical_terms", "supplied_release_terms", release.TermsVersion, true, count, null, false, 0, null, "not_subscribed", "not_configured", notices.ToArray());
        var effectiveNow = state.LastSeenUtc > now ? state.LastSeenUtc.Value : now;
        var declaration = state.Declarations.LastOrDefault();
        if (declaration is not null && declaration.ReviewDueAtUtc <= effectiveNow) notices.Add("review_due");
        var paid = entitlement?.OrganisationId == state.Organisation?.Id && entitlement?.NotBeforeUtc <= effectiveNow && entitlement?.ExpiresAtUtc > effectiveNow;
        if (paid)
        {
            if (count > entitlement!.ServerCapacity) notices.Add("over_capacity");
            return new(release.LegalRegime, "professional_active", "professional", release.TermsVersion, true, count, entitlement!.ServerCapacity, count >= 250, 0, null, "not_subscribed", "not_configured", notices.ToArray());
        }
        if (declaration is null || declaration.RevenueBand == "unknown" && declaration.Category == "internal_business" && !declaration.ThirdPartyCommercialUse)
            return Make("declaration_required", "undetermined", 0);
        if (CommunityEligible(declaration)) return Make("community_eligible", "community", 0);
        if (entitlement?.ExpiresAtUtc <= effectiveNow) return Make("professional_expired", "professional", 0);
        var remaining = state.Evaluation is { } trial ? trial.EndsAtUtc - effectiveNow : TimeSpan.Zero;
        return Make(remaining > TimeSpan.Zero ? "evaluation_active" : "professional_required", "professional",
            remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds / 86400) : 0);
        LicensingStatus Make(string status, string required, int days) => new(release.LegalRegime, status, required,
            release.TermsVersion, true, count, entitlement?.ServerCapacity, count >= 250, days,
            state.Evaluation?.EndsAtUtc, "not_subscribed", "not_configured", notices.ToArray());
    }
}
