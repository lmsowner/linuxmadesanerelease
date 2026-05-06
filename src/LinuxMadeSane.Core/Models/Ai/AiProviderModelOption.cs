using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderModelOption(
    AiProviderType ProviderType,
    string ModelId,
    string DisplayName,
    string Description,
    bool SupportsToolInvocation,
    bool IsRecommendedDefault);
