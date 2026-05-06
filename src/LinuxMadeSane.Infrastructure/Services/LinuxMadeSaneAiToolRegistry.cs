using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LinuxMadeSaneAiToolRegistry : IAiToolRegistry
{
    private static readonly IReadOnlyList<AiToolDefinition> Definitions =
    [
        CreateDefinition<ListServersToolRequest, ListServersToolResponse>(
            AiToolNames.ListServers,
            "List the Linux servers available to the current AI chat, including attachment state and connection posture.",
            AiActionRiskLevel.ReadOnly,
            AiApprovalRequirement.AutoRun,
            false,
            "IManagedHostStore"),
        CreateDefinition<GetServerSummaryToolRequest, GetServerSummaryToolResponse>(
            AiToolNames.GetServerSummary,
            "Return the saved Linux Made Sane summary for a specific server, including SSH capability metadata without exposing secrets.",
            AiActionRiskLevel.ReadOnly,
            AiApprovalRequirement.AutoRun,
            false,
            "IManagedHostStore"),
        CreateDefinition<GetServerHealthToolRequest, GetServerHealthToolResponse>(
            AiToolNames.GetServerHealth,
            "Collect a lightweight live health snapshot from a Linux server over SSH.",
            AiActionRiskLevel.ReadOnly,
            AiApprovalRequirement.AutoRun,
            true,
            "ICommandExecutionService"),
        CreateDefinition<ListServicesToolRequest, ListServicesToolResponse>(
            AiToolNames.ListServices,
            "List systemd services on a Linux server over SSH.",
            AiActionRiskLevel.ReadOnly,
            AiApprovalRequirement.AutoRun,
            true,
            "ICommandExecutionService"),
        CreateDefinition<RestartServiceToolRequest, RestartServiceToolResponse>(
            AiToolNames.RestartService,
            "Restart a named systemd service on a Linux server.",
            AiActionRiskLevel.MediumRiskMutation,
            AiApprovalRequirement.UserConfirmation,
            true,
            "ICommandExecutionService"),
        CreateDefinition<BrowseDirectoryToolRequest, BrowseDirectoryToolResponse>(
            AiToolNames.BrowseDirectory,
            "Browse a remote directory over SFTP using the server's stored Linux Made Sane credentials.",
            AiActionRiskLevel.ReadOnly,
            AiApprovalRequirement.AutoRun,
            false,
            "ISftpFileBrowsingService"),
        CreateDefinition<ReadFileToolRequest, ReadFileToolResponse>(
            AiToolNames.ReadFile,
            "Read the contents of a remote file over SFTP using the server's stored Linux Made Sane credentials.",
            AiActionRiskLevel.ReadOnly,
            AiApprovalRequirement.AutoRun,
            false,
            "ISftpFileBrowsingService"),
        CreateDefinition<RunCommandToolRequest, RunCommandToolResponse>(
            AiToolNames.RunCommand,
            "Run an arbitrary shell command on a Linux server over SSH.",
            AiActionRiskLevel.Privileged,
            AiApprovalRequirement.AdminApproval,
            true,
            "ICommandExecutionService",
            supportsRememberDecision: false),
        CreateDefinition<WriteFileWithConfirmationToolRequest, WriteFileWithConfirmationToolResponse>(
            AiToolNames.WriteFileWithConfirmation,
            "Write text content to a remote file over SFTP after explicit approval.",
            AiActionRiskLevel.HighRiskMutation,
            AiApprovalRequirement.UserConfirmation,
            false,
            "ISftpFileBrowsingService"),
        CreateDefinition<InstallPackageWithConfirmationToolRequest, InstallPackageWithConfirmationToolResponse>(
            AiToolNames.InstallPackageWithConfirmation,
            "Install packages with apt-get on a Linux server after explicit approval.",
            AiActionRiskLevel.Privileged,
            AiApprovalRequirement.AdminApproval,
            true,
            "ICommandExecutionService",
            supportsRememberDecision: false),
        CreateDefinition<RollbackSafeChangeToolRequest, RollbackSafeChangeToolResponse>(
            AiToolNames.RollbackSafeChange,
            "Rollback a previously snapshotted Deep Fix change after approval.",
            AiActionRiskLevel.HighRiskMutation,
            AiApprovalRequirement.UserConfirmation,
            true,
            "IAiSafeChangeService",
            supportsRememberDecision: false,
            isProviderVisible: false)
    ];

    public IReadOnlyList<AiToolDefinition> ListPublishedTools(
        AiChatThread thread,
        IReadOnlyList<AiAttachedServer> attachedServers) =>
        Definitions.Where(definition => definition.IsProviderVisible).ToArray();

    public AiToolDefinition? FindTool(string toolName) =>
        Definitions.FirstOrDefault(definition => definition.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));

    private static AiToolDefinition CreateDefinition<TRequest, TResponse>(
        string name,
        string description,
        AiActionRiskLevel riskLevel,
        AiApprovalRequirement minimumRequirement,
        bool requiresCommandPreview,
        string executionPath,
        bool supportsRememberDecision = true,
        bool isProviderVisible = true)
        where TRequest : IAiToolRequest
        where TResponse : IAiToolResponse =>
        new(
            name,
            description,
            typeof(TRequest),
            typeof(TResponse),
            new AiToolApprovalMetadata(
                riskLevel,
                minimumRequirement,
                requiresCommandPreview,
                minimumRequirement is not AiApprovalRequirement.AutoRun,
                supportsRememberDecision && minimumRequirement is not AiApprovalRequirement.AutoRun),
            executionPath,
            isProviderVisible);
}
