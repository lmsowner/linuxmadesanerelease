// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Claims;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class ConnectionProfileUserResolver(ISecurityUserStore securityUserStore)
{
    public async Task<SecurityUser?> ResolveAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken = default)
    {
        var userIdValue = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userIdValue, out var userId))
        {
            return await securityUserStore.GetAsync(userId, cancellationToken);
        }

        if (principal?.Identity?.IsAuthenticated == true)
        {
            return null;
        }

        var users = await securityUserStore.ListAsync(cancellationToken);
        return users
            .Where(user => user.IsEnabled)
            .OrderBy(user => user.CreatedAtUtc)
            .ThenBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
