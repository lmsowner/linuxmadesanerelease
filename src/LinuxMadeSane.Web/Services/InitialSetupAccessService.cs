// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class InitialSetupAccessService(
    ISecurityUserStore users,
    ISecurityPasskeyStore passkeys,
    ITrustedNetworkStore networks,
    ITrustedNetworkAccessService networkAccess,
    TemporarySetupAuthorizationService authorization,
    LocalAccessRecoveryService recovery)
{
    public async Task<bool> HasUsableLoginAsync(CancellationToken token = default)
    {
        foreach (var user in (await users.ListAsync(token)).Where(user => user.IsEnabled))
            if (!string.IsNullOrWhiteSpace(user.OtpSecretReference) || (await passkeys.ListByUserAsync(user.Id, token)).Count > 0)
                return true;
        return false;
    }

    public Task<bool> HasRecoveryAccessAsync(HttpContext context) =>
        recovery.IsTemporarySetupSessionActiveAsync(authorization.GetAuthorizationId(context.Request), context.RequestAborted);

    public async Task<bool> CanConfigureAsync(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true || await HasRecoveryAccessAsync(context) ||
        (await networkAccess.EvaluateAsync(context.Connection.RemoteIpAddress, context.Request.Host.Host, context.RequestAborted)).IsTrusted;

    public async Task<TrustedNetworkEntry?> GetCurrentLocalNetworkAsync(HttpContext context)
    {
        // Never offer an anonymous public fallback or take a browser-supplied rule ID.
        if (!LocalRequestTargetEvaluator.IsLocal(context.Connection.RemoteIpAddress?.ToString())) return null;
        var entry = TrustedNetworkMatcher.Match(context.Connection.RemoteIpAddress, await networks.ListAsync(context.RequestAborted));
        return entry is { IsEnabled: true } && entry.AddressOrCidr is not ("0.0.0.0/0" or "::/0") ? entry : null;
    }
}
