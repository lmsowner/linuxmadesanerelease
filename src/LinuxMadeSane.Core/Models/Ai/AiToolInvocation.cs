using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolInvocation(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    Guid? ExecutionPlanId,
    Guid? ProposedActionId,
    string ToolName,
    string ArgumentsJson,
    AiInvocationStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    AiToolResult? Result);
