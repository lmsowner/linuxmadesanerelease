using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiChatWorkspaceViewModel(
    AiChatThread Thread,
    IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiAttachedServer> AttachedServers,
    IReadOnlyList<AiChatRun> ChatRuns,
    IReadOnlyList<AiExecutionPlan> ExecutionPlans,
    IReadOnlyList<AiApprovalRequest> ApprovalRequests,
    IReadOnlyList<AiToolInvocation> ToolInvocations,
    IReadOnlyList<AiAuditEntry> AuditEntries,
    IReadOnlyList<AiChatCheckpoint> Checkpoints,
    IReadOnlyList<ManagedHost> AvailableServers,
    IReadOnlyList<AiProviderDefinition> SupportedProviders,
    IReadOnlyList<AiConfiguredProviderViewModel> ConfiguredProviders,
    IReadOnlyList<AiModelDefinition> Models,
    IReadOnlyList<string> PublishedTools)
{
    public IReadOnlyList<AiChatTimelineItemViewModel> Timeline { get; init; } = [];
}
