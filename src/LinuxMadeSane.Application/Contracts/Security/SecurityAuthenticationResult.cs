// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityAuthenticationResult(
    bool Succeeded,
    Guid? UserId,
    string? Email,
    int SessionLifetimeMinutes,
    string? FailureMessage)
{
    public static SecurityAuthenticationResult Success(Guid userId, string email, int sessionLifetimeMinutes) =>
        new(true, userId, email, SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(sessionLifetimeMinutes), null);

    public static SecurityAuthenticationResult Failure(string message) =>
        new(false, null, null, SecuritySessionPolicy.DefaultSessionLifetimeMinutes, message);
}
