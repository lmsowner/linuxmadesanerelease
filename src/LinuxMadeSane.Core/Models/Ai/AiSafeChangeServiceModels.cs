namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiSafeChangeExecutionResult(
    AiProposedAction UpdatedAction,
    AiToolExecutionResult ExecutionResult);
