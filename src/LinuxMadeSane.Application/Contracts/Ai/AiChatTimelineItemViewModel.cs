using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public abstract record AiChatTimelineItemViewModel(DateTimeOffset OccurredAtUtc);

public sealed record AiChatMessageTimelineItemViewModel(
    AiChatMessage Message) : AiChatTimelineItemViewModel(Message.CreatedAtUtc);

public sealed record AiChatToolExecutionTimelineItemViewModel(
    Guid InvocationId,
    Guid? ProposedActionId,
    string ToolName,
    string Title,
    string Summary,
    AiInvocationStatus Status,
    AiExecutionOutcome Outcome,
    AiActionRiskLevel RiskLevel,
    string ApprovalStatus,
    string TargetServerName,
    string CommandText,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string OutputText,
    string PayloadText,
    bool IsCommandExecution,
    bool SupportsProgressiveOutput,
    bool IsProgressiveOutputAvailable)
    : AiChatTimelineItemViewModel(CompletedAtUtc ?? StartedAtUtc)
{
    public AiSafeChangeState? SafeChange { get; init; }

    public bool IsFailure =>
        Outcome is AiExecutionOutcome.Failed or AiExecutionOutcome.Cancelled or AiExecutionOutcome.Rejected;

    public bool HasStructuredOutput =>
        !string.IsNullOrWhiteSpace(StandardOutput) ||
        !string.IsNullOrWhiteSpace(StandardError) ||
        !string.IsNullOrWhiteSpace(OutputText) ||
        !string.IsNullOrWhiteSpace(PayloadText);
}
