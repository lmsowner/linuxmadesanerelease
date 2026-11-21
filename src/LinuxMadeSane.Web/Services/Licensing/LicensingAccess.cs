// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Claims;
using LinuxMadeSane.Core.Models;
namespace LinuxMadeSane.Web.Services.Licensing;
public sealed class LicensingAccess(IConfiguration config)
{
    // LMS has no general administrator/billing-role claim model. Reuse trusted-local administration;
    // remote administration needs an explicit existing LMS user-ID allowlist, not merely a login.
    public bool CanAdminister(ClaimsPrincipal user, bool trustedLocal) => trustedLocal || Listed(user, "Licensing:AdministratorUserIds");
    public bool CanBill(ClaimsPrincipal user, bool trustedLocal) => CanAdminister(user, trustedLocal) || Listed(user, "Licensing:BillingUserIds");
    public static bool Trusted(HttpContext context) => context.User.Identity?.IsAuthenticated != true &&
        context.Items["LmsTrustedNetworkAccess"] is TrustedNetworkAccessResult { IsTrusted: true };
    public static string Actor(ClaimsPrincipal user, bool trustedLocal) => trustedLocal ? "trusted-local-administrator" :
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
    private bool Listed(ClaimsPrincipal user, string key) => user.Identity?.IsAuthenticated == true &&
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty &&
        config.GetSection(key).Get<string[]>()?.Any(v => Guid.TryParse(v, out var allowed) && allowed == id) == true;
}
