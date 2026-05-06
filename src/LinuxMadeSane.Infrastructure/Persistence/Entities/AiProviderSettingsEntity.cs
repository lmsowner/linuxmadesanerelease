namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiProviderSettingsEntity
{
    public string ProviderKey { get; set; } = string.Empty;
    public int ProviderType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string DefaultModelId { get; set; } = string.Empty;
    public bool StreamingEnabled { get; set; }
    public bool ToolUseEnabled { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = string.Empty;
    public string ApiKeySecretReference { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
