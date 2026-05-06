namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class PortalConnectionSettingsEntity
{
    public int Id { get; set; }
    public Guid LocalInstanceId { get; set; }
    public string PortalBaseUrl { get; set; } = string.Empty;
    public string InstanceDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? InstanceIdentityPrivateKeySecretReference { get; set; }
    public string InstanceIdentityPublicKey { get; set; } = string.Empty;
    public string InstanceIdentityPublicKeyFingerprint { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public DateTimeOffset? PairingCodeGeneratedAtUtc { get; set; }
    public DateTimeOffset? PairingCodeExpiresAtUtc { get; set; }
    public Guid? PortalOrganizationId { get; set; }
    public string? PortalOrganizationName { get; set; }
    public Guid? PortalInstanceId { get; set; }
    public string? PortalApiKeyId { get; set; }
    public string? PortalApiSecretReference { get; set; }
    public string LastConnectionStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastConnectedAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
