namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiModelDefinition(
    string ProviderKey,
    string ModelId,
    string DisplayName,
    string Description,
    int? ContextWindow,
    bool SupportsToolInvocation);
