namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderToolCallRequest(
    string ProviderToolCallId,
    string ToolName,
    string ArgumentsJson);
