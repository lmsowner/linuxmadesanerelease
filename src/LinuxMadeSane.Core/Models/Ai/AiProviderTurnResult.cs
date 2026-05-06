namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderTurnResult(
    string ProviderResponseId,
    string? ConversationReference,
    string? ModelId,
    IReadOnlyList<AiProviderAssistantOutput> AssistantOutputs,
    IReadOnlyList<AiProviderToolCallRequest> ToolCalls);
