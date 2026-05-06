namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalAiEngineSettingsEntity
{
    public int Id { get; set; }
    public int RuntimeKind { get; set; }
    public string RuntimeEndpoint { get; set; } = string.Empty;
    public string DefaultModelId { get; set; } = string.Empty;
    public string LocalProviderKey { get; set; } = string.Empty;
    public bool SharingEnabled { get; set; }
    public bool AllowOrganizationInstances { get; set; }
    public string AllowedOrganizationIdsJson { get; set; } = "[]";
    public string AllowedInstanceIdsJson { get; set; } = "[]";
    public string AllowedModelIdsJson { get; set; } = "[]";
    public int MaxConcurrentRequests { get; set; }
    public int MaxQueuedRequests { get; set; }
    public int MaxRequestsPerMinute { get; set; }
    public int MaxPromptCharacters { get; set; }
    public int RequestTimeoutSeconds { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastSharedAtUtc { get; set; }
}
