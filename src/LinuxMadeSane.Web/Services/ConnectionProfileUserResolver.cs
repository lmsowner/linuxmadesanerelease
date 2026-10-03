// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Claims;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class ConnectionProfileUserResolver(ISecurityUserStore securityUserStore,
    ISavedCredentialAccessContext? accessContext = null)
{
    public async Task<SecurityUser?> ResolveAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken = default)
    {
        var userIdValue = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal?.Identity?.IsAuthenticated == true && Guid.TryParse(userIdValue, out var userId))
        {
            var user = await securityUserStore.GetAsync(userId, cancellationToken);
            return user?.IsEnabled == true ? user : null;
        }

        if (principal?.Identity?.IsAuthenticated == true)
        {
            return null;
        }

        // Legacy Connect As profiles were stored under the first enabled account.
        // Preserve that ownership only for explicitly verified host-admin access.
        if (accessContext is null || await accessContext.GetAuthenticatedUserIdAsync(cancellationToken) != Guid.Empty)
            return null;

        return (await securityUserStore.ListAsync(cancellationToken))
            .Where(user => user.IsEnabled)
            .OrderBy(user => user.CreatedAtUtc)
            .ThenBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
