using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiConfiguredProviderViewModel(
    string ProviderKey,
    AiProviderType ProviderType,
    string DisplayName,
    bool IsEnabled,
    bool IsDefault,
    string DefaultModelId,
    bool StreamingEnabled,
    bool ToolUseEnabled,
    bool HasApiKeyConfigured,
    bool RequiresApiKey);
