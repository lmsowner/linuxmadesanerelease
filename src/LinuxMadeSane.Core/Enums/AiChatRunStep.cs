namespace LinuxMadeSane.Core.Enums;

public enum AiChatRunStep
{
    Queued = 0,
    RequestingProviderTurn = 1,
    ProcessingProviderResponse = 2,
    EvaluatingToolCalls = 3,
    AwaitingApproval = 4,
    ExecutingApprovedTools = 5,
    ContinuingProviderTurn = 6,
    Completed = 7,
    Failed = 8,
    Cancelled = 9
}
