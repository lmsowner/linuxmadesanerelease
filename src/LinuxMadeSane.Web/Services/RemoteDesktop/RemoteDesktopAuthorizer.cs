// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Claims;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;
public sealed class RemoteDesktopAuthorizer(ISecurityUserStore users, IRemoteDesktopConnectionStore connections,
    IManagedHostStore hosts, LinuxMadeSaneDbContext db, ITrustedNetworkAccessService? networks = null)
{
    public async Task<bool> ValidAsync(RemoteDesktopSession session, CancellationToken token = default)
    {
        if (session.Revoked.IsCancellationRequested || session.Expires <= DateTimeOffset.UtcNow) return false;
        var owner = session.Connection.OwnerUserId;
        if (owner == Guid.Empty)
        {
            if (networks is null || !System.Net.IPAddress.TryParse(session.TrustedRemoteAddress,out var address) ||
                !(await networks.EvaluateAsync(address,session.TrustedRequestHost,token)).IsTrusted) return false;
        }
        else
        {
            var user = await users.GetAsync(owner, token);
            if (user?.IsEnabled != true || user.UpdatedAtUtc != session.UserUpdatedAtUtc) return false;
        }
        var current = await connections.GetAsync(session.Connection.Id, token);
        if (current is null || current.OwnerUserId != owner || current != session.Connection ||
            current.HostId.HasValue && !LinuxMadeSane.Core.Models.Ai.AiLocalMachine.IsLocalMachine(current.HostId.Value) && await hosts.GetAsync(current.HostId.Value, token) is null) return false;
        if (current.Options.ManagedLmsDesktop)
            return session.ManagedTransport is { IsActive: true, ShareLease: not null };
        return await db.SavedConnectionCredentials.AsNoTracking().AnyAsync(c => c.Id == current.CredentialId &&
            c.Kind == RemoteDesktopConnections.CredentialKind(current.Protocol) && c.PasswordReference != null && db.ProtectedSecrets.Any(s => s.Reference == c.PasswordReference), token);

    }
    public static Guid UserId(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true &&
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty ? id : throw new UnauthorizedAccessException("An authenticated LMS session is required.");
}
