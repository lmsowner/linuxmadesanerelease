namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record TerminalAiTurnResult(
    string ProviderKey,
    string ProviderLabel,
    string ModelId,
    string ProviderConversationReference,
    string ProviderResponseId,
    AiPromptSanitizationSummary Sanitization,
    TerminalAiConversationEntry UserEntry,
    TerminalAiConversationEntry AssistantEntry);
