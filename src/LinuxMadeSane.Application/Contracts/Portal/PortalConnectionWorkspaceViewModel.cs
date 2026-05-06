namespace LinuxMadeSane.Application.Contracts.Portal;

public sealed record PortalConnectionWorkspaceViewModel(
    PortalConnectionEditor Editor,
    Guid LocalInstanceId,
    bool IsPaired,
    string PairingCode,
    string PairingQrPayload,
    string InstanceIdentityFingerprint,
    string ConnectionState,
    string StatusMessage,
    DateTimeOffset? PairingCodeExpiresAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    Guid? PortalOrganizationId,
    string? PortalOrganizationName,
    Guid? PortalInstanceId);
