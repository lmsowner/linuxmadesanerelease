// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Interfaces;

public sealed record UserCredentialTrial(Guid Id, Guid UserId, string UserName, DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc, bool ChangesPassword)
{
    public RemoteAccessSshAuthenticationMode? ProposedMode { get; init; }
    public string? ProposedPublicKeys { get; init; }
    public int? SshPort { get; init; }
}

public interface IUserCredentialTrialService
{
    Task<UserCredentialTrial> StartTrialAsync(Guid userId, LocalUserAccessEditor? editor, string? newPassword = null, CancellationToken cancellationToken = default);
    Task<UserCredentialTrial?> GetActiveTrialAsync(CancellationToken cancellationToken = default);
    Task ConfirmAsync(Guid trialId, CancellationToken cancellationToken = default);
    Task RevertAsync(Guid trialId, CancellationToken cancellationToken = default);
}
