// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Licensing;
using LinuxMadeSane.Web.Services.Licensing;
using Microsoft.AspNetCore.Antiforgery;
namespace LinuxMadeSane.Web;
public static class LicensingEndpointExtensions
{
    public static void MapLicensing(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/licensing").RequireRateLimiting("lms-licensing").AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var access = context.RequestServices.GetRequiredService<LicensingAccess>();
            var trusted = LicensingAccess.Trusted(context);
            if (!trusted && context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            if (context.Request.Method != "GET")
            {
                if (!access.CanAdminister(context.User, trusted)) return Results.StatusCode(403);
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "csrf_failed" }); }
            }
            try { return await next(invocation); }
            catch (ArgumentException) { return Results.BadRequest(new { code = "invalid_licensing_input", message = "The organisation, declaration or imported record is invalid. Previous valid records are retained." }); }
            catch (InvalidOperationException) { return Results.Conflict(new { code = "revision_conflict" }); }
        });
        group.MapGet("/status", async (HttpContext c, LocalLicensingService service, LicensingAccess access) =>
        {
            var status = await service.StatusAsync();
            if (!access.CanAdminister(c.User, LicensingAccess.Trusted(c)))
                return Results.Ok(new { status.Status, status.LegalRegime, status.TermsVersion, status.ManagementAllowed });
            return Results.Ok(status);
        });
        group.MapGet("/admin", async (HttpContext c, LocalLicensingService service, LicensingAccess access, IAntiforgery csrf) =>
        {
            if (!access.CanAdminister(c.User, LicensingAccess.Trusted(c))) return Results.StatusCode(403);
            var (state, revision) = await service.ReadAsync();
            return Results.Ok(new { state, revision, csrfToken = csrf.GetAndStoreTokens(c).RequestToken, preview = await service.StatusAsync(true) });
        });
        group.MapPost("/declarations", async (HttpContext c, LocalLicensingService service) =>
        {
            var request = await ReadAsync<DeclarationRequest>(c);
            await service.DeclareAsync(request.Input, Actor(c), request.Revision);
            return Results.Ok(await service.StatusAsync());
        });
        group.MapPost("/organisation/import", async (HttpContext c, LocalLicensingService service) =>
        { var request = await ReadAsync<TransferRequest>(c); await service.ImportOrganisationAsync(request.Transfer, Actor(c), request.Reason, request.Revision); return Results.Ok(await service.StatusAsync()); });
        group.MapPost("/evaluation/start", async (HttpContext c, LocalLicensingService service) =>
        { await service.StartAsync(Actor(c)); return Results.Ok((await service.ReadAsync()).State.Evaluation); });
        group.MapPost("/evaluation/import", async (HttpContext c, LocalLicensingService service) =>
        {
            var request = await ReadAsync<EvaluationRequest>(c);
            await service.ImportEvaluationAsync(request.Evaluation, Actor(c), request.Reason, request.Revision);
            return Results.Ok(await service.StatusAsync());
        });
        group.MapPost("/entitlements/import", async (HttpContext c, LocalLicensingService service) =>
        {
            var request = await ReadAsync<EntitlementRequest>(c);
            try { await service.ImportAsync(request.Envelope, Actor(c), request.Revision); }
            catch (ArgumentException) { return Results.UnprocessableEntity(new { code = "invalid_entitlement", message = "Licence rejected; previous valid licence retained." }); }
            return Results.Ok(await service.StatusAsync());
        });
        group.MapGet("/entitlements/export", async (HttpContext c, LocalLicensingService service, LicensingAccess access) =>
        {
            if (!access.CanAdminister(c.User, LicensingAccess.Trusted(c))) return Results.StatusCode(403);
            var (state, _) = await service.ReadAsync();
            c.Response.Headers.CacheControl = "no-store";
            return Results.Json(new OrganisationTransfer(1, state.Organisation!, state.Declarations.ToArray(), state.Evaluation, state.Licence));
        });
        group.MapPost("/servers/reconcile", async (HttpContext c, LocalLicensingService service) =>
        { var request = await ReadAsync<ServerRequest>(c); await service.ReconcileServerAsync(request.Server, Actor(c), request.Revision); return Results.Ok(await service.StatusAsync()); });
        group.MapPost("/notices/dismiss", async (HttpContext c, LocalLicensingService service) =>
        { var request = await ReadAsync<DismissRequest>(c); await service.DismissAsync(Actor(c), request.NoticeVersion); return Results.NoContent(); });
        app.MapGet("/api/v1/pricing", () => Results.Ok(new { status = "published", checkoutAvailable = false, priceBook = PriceBook.Proposed, portal = "Contact for Portal pricing" }));
        app.MapPost("/api/v1/billing/preview", async (HttpContext c, LicensingAccess access, IAntiforgery csrf) =>
        {
            if (!access.CanBill(c.User, LicensingAccess.Trusted(c))) return Results.StatusCode(403);
            try { await csrf.ValidateRequestAsync(c); var request = await ReadAsync<PricingRequest>(c); return Results.Ok(ProfessionalPricing.Quote(request.Servers)); }
            catch (Exception ex) when (ex is ArgumentException or AntiforgeryValidationException) { return Results.BadRequest(new { code = "invalid_preview" }); }
        });
        // Release gates are deliberate: no draft offer, account signup or payment side effect.
        foreach (var route in new[] { "orders", "checkout", "webhooks/stripe" })
            app.MapPost("/api/v1/billing/" + route, () => Results.Problem(statusCode: 503, title: "Commercial approval and verified seller/payment configuration required", extensions: new Dictionary<string, object?> { ["code"] = "billing_not_approved" }));
    }
    private static string Actor(HttpContext c) => LicensingAccess.Actor(c.User, LicensingAccess.Trusted(c));
    public static async Task<T> ReadAsync<T>(HttpContext c)
    {
        using var buffer = new MemoryStream();
        var block = new byte[4096]; int count;
        while ((count = await c.Request.Body.ReadAsync(block, c.RequestAborted)) > 0)
        { if (buffer.Length + count > 65536) throw new ArgumentException("Input exceeds 64 KiB."); buffer.Write(block, 0, count); }
        try { return JsonSerializer.Deserialize<T>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Missing input."); }
        catch (JsonException) { throw new ArgumentException("Malformed input."); }
    }
    public sealed record DeclarationRequest(DeclarationInput Input, long Revision);
    public sealed record EvaluationRequest(Evaluation Evaluation, string Reason, long Revision);
    public sealed record EntitlementRequest(SignedEntitlement Envelope, long Revision);
    public sealed record DismissRequest(string NoticeVersion);
    public sealed record TransferRequest(OrganisationTransfer Transfer, string Reason, long Revision);
    public sealed record ServerRequest(ServerIdentity Server, long Revision);
    public sealed record PricingRequest(int Servers);
}
