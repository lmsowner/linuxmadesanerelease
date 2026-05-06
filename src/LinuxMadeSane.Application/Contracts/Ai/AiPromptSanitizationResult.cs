namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiPromptSanitizationResult(
    string Content,
    AiPromptSanitizationSummary Summary);
