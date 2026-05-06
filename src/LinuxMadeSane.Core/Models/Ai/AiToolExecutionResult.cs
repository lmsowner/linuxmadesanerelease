namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolExecutionResult(
    AiToolDefinition Definition,
    IAiToolResponse Response,
    AiToolResult PersistedResult);
