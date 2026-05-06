namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderTurnRequest(
    AiChatThread Thread,
    IReadOnlyList<AiChatMessage> MessageHistory,
    IReadOnlyList<AiAttachedServer> AttachedServers,
    IReadOnlyList<AiProviderInputItem> InputItems,
    IReadOnlyList<AiToolDefinition> AvailableTools,
    bool StreamingEnabled,
    bool InternetResearchAllowed);
