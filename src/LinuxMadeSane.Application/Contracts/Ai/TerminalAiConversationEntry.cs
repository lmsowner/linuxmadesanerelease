using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record TerminalAiConversationEntry(
    AiChatMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc,
    string SuggestedCommand = "");
