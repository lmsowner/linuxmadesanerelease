namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderToolCallInputItem(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson) : AiProviderInputItem;
