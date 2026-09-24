// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Claims;

namespace LinuxMadeSane.Web.Services;

public static class AccountAuthenticationMethod
{
    public static string GetLabel(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return "Direct access";
        }

        var methods = principal.FindAll("amr").Select(claim => claim.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (methods.Contains("passkey")) return "Passkey";
        if (methods.Contains("otp")) return "Authenticator code";
        if (methods.Contains("email-link")) return "Email link";
        if (methods.Contains("email")) return "Email code";
        if (methods.Contains("local-recovery")) return "Local recovery";
        return "Authenticated session";
    }
}
