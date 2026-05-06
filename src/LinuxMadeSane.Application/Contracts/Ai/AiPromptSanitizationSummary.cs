namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiPromptSanitizationSummary(
    bool Applied,
    bool TrustedLocalProvider,
    int RedactionCount,
    IReadOnlyList<string> Categories,
    string Message);
