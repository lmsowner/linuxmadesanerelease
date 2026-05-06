using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

internal static class AiChatTimelineViewModelMapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static IReadOnlyList<AiChatTimelineItemViewModel> Map(
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiAttachedServer> attachedServers,
        IReadOnlyList<AiExecutionPlan> executionPlans,
        IReadOnlyList<AiApprovalRequest> approvalRequests,
        IReadOnlyList<AiToolInvocation> toolInvocations)
    {
        var actionById = executionPlans
            .SelectMany(plan => plan.Actions)
            .GroupBy(action => action.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var approvalByActionId = approvalRequests
            .Where(request => request.ProposedActionId.HasValue)
            .GroupBy(request => request.ProposedActionId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(request => request.RequestedAtUtc)
                    .First());
        var attachedServerNames = attachedServers
            .GroupBy(server => server.ManagedHostId)
            .ToDictionary(group => group.Key, group => group.First().ServerName);

        var items = new List<AiChatTimelineItemViewModel>(messages.Count + toolInvocations.Count);
        items.AddRange(messages
            .Where(message => message.Role != AiChatMessageRole.Tool)
            .Select(message => new AiChatMessageTimelineItemViewModel(message)));
        items.AddRange(toolInvocations.Select(invocation =>
            MapToolExecution(
                invocation,
                actionById,
                approvalByActionId,
                attachedServerNames)));

        return items
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item is AiChatMessageTimelineItemViewModel messageItem ? messageItem.Message.SequenceNumber : int.MaxValue)
            .ToArray();
    }

    private static AiChatToolExecutionTimelineItemViewModel MapToolExecution(
        AiToolInvocation invocation,
        IReadOnlyDictionary<Guid, AiProposedAction> actionById,
        IReadOnlyDictionary<Guid, AiApprovalRequest> approvalByActionId,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var action = invocation.ProposedActionId.HasValue
            ? actionById.GetValueOrDefault(invocation.ProposedActionId.Value)
            : null;
        var approval = invocation.ProposedActionId.HasValue
            ? approvalByActionId.GetValueOrDefault(invocation.ProposedActionId.Value)
            : null;
        var result = invocation.Result;
        var completedAtUtc = invocation.CompletedAtUtc ?? result?.CompletedAtUtc;
        var details = BuildToolDetails(invocation, action, result, attachedServerNames);

        return new AiChatToolExecutionTimelineItemViewModel(
            invocation.Id,
            invocation.ProposedActionId,
            invocation.ToolName,
            action?.Title ?? BuildToolTitle(invocation.ToolName),
            result?.Summary ?? BuildActiveSummary(invocation),
            invocation.Status,
            result?.Outcome ?? MapOutcome(invocation.Status),
            action?.RiskLevel ?? AiActionRiskLevel.ReadOnly,
            BuildApprovalStatus(action, approval),
            details.TargetServerName,
            details.CommandText,
            invocation.StartedAtUtc,
            completedAtUtc,
            result?.ExitCode,
            details.StandardOutput,
            details.StandardError,
            details.OutputText,
            details.PayloadText,
            details.IsCommandExecution,
            false,
            false)
        {
            SafeChange = action?.SafeChange
        };
    }

    private static ToolDetails BuildToolDetails(
        AiToolInvocation invocation,
        AiProposedAction? action,
        AiToolResult? result,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        if (result is null)
        {
            return new ToolDetails(
                ResolveTargetServerName(invocation.ToolName, invocation.ArgumentsJson, attachedServerNames),
                ResolveCommandText(invocation.ToolName, invocation.ArgumentsJson, action?.CommandPreview),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                AiCommandDisplayFormatter.IsCommandExecutionTool(invocation.ToolName));
        }

        return invocation.ToolName switch
        {
            AiToolNames.RunCommand => BuildRunCommandDetails(result, action, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.RestartService => BuildRestartServiceDetails(result, action, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.InstallPackageWithConfirmation => BuildInstallPackageDetails(result, action, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.ListServices => BuildListServicesDetails(result, action, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.GetServerHealth => BuildServerHealthDetails(result, action, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.WriteFileWithConfirmation => BuildWriteFileDetails(result, action, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.ReadFile => BuildReadFileDetails(result, invocation.ArgumentsJson, attachedServerNames),
            AiToolNames.BrowseDirectory => BuildGenericDetails(result, invocation.ToolName, invocation.ArgumentsJson, attachedServerNames, action, false),
            AiToolNames.ListServers => BuildGenericDetails(result, invocation.ToolName, invocation.ArgumentsJson, attachedServerNames, action, false),
            AiToolNames.GetServerSummary => BuildGenericDetails(result, invocation.ToolName, invocation.ArgumentsJson, attachedServerNames, action, false),
            _ => BuildGenericDetails(result, invocation.ToolName, invocation.ArgumentsJson, attachedServerNames, action, false)
        };
    }

    private static ToolDetails BuildRunCommandDetails(
        AiToolResult result,
        AiProposedAction? action,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<RunCommandToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.RunCommand, argumentsJson, attachedServerNames),
            ResolveCommandText(AiToolNames.RunCommand, argumentsJson, action?.CommandPreview, response?.CommandText),
            response?.StandardOutput ?? string.Empty,
            response?.StandardError ?? result.ErrorText,
            string.Empty,
            string.Empty,
            true);
    }

    private static ToolDetails BuildRestartServiceDetails(
        AiToolResult result,
        AiProposedAction? action,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<RestartServiceToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.RestartService, argumentsJson, attachedServerNames),
            ResolveCommandText(AiToolNames.RestartService, argumentsJson, action?.CommandPreview, response?.CommandText),
            response?.StandardOutput ?? string.Empty,
            response?.StandardError ?? result.ErrorText,
            string.Empty,
            string.Empty,
            true);
    }

    private static ToolDetails BuildInstallPackageDetails(
        AiToolResult result,
        AiProposedAction? action,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<InstallPackageWithConfirmationToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.InstallPackageWithConfirmation, argumentsJson, attachedServerNames),
            ResolveCommandText(AiToolNames.InstallPackageWithConfirmation, argumentsJson, action?.CommandPreview, response?.CommandText),
            response?.StandardOutput ?? string.Empty,
            response?.StandardError ?? result.ErrorText,
            string.Empty,
            string.Empty,
            true);
    }

    private static ToolDetails BuildListServicesDetails(
        AiToolResult result,
        AiProposedAction? action,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<ListServicesToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.ListServices, argumentsJson, attachedServerNames),
            ResolveCommandText(AiToolNames.ListServices, argumentsJson, action?.CommandPreview),
            response?.RawOutput ?? string.Empty,
            result.ErrorText,
            string.Empty,
            string.Empty,
            true);
    }

    private static ToolDetails BuildServerHealthDetails(
        AiToolResult result,
        AiProposedAction? action,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<GetServerHealthToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.GetServerHealth, argumentsJson, attachedServerNames),
            ResolveCommandText(AiToolNames.GetServerHealth, argumentsJson, action?.CommandPreview),
            response?.RawOutput ?? string.Empty,
            result.ErrorText,
            result.OutputText,
            string.Empty,
            true);
    }

    private static ToolDetails BuildWriteFileDetails(
        AiToolResult result,
        AiProposedAction? action,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<WriteFileWithConfirmationToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.WriteFileWithConfirmation, argumentsJson, attachedServerNames),
            AiCommandDisplayFormatter.SanitizeDisplayText(action?.CommandPreview ?? string.Empty),
            string.Empty,
            result.ErrorText,
            result.OutputText,
            PrettyJson(result.PayloadJson),
            false);
    }

    private static ToolDetails BuildReadFileDetails(
        AiToolResult result,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var response = DeserializePayload<ReadFileToolResponse>(result.PayloadJson);
        return new ToolDetails(
            response?.ServerName ?? ResolveTargetServerName(AiToolNames.ReadFile, argumentsJson, attachedServerNames),
            string.Empty,
            string.Empty,
            result.ErrorText,
            response?.Content ?? result.OutputText,
            PrettyJson(result.PayloadJson),
            false);
    }

    private static ToolDetails BuildGenericDetails(
        AiToolResult result,
        string toolName,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames,
        AiProposedAction? action,
        bool isCommandExecution)
    {
        return new ToolDetails(
            ResolveTargetServerName(toolName, argumentsJson, attachedServerNames),
            ResolveCommandText(toolName, argumentsJson, action?.CommandPreview),
            string.Empty,
            result.ErrorText,
            result.OutputText,
            PrettyJson(result.PayloadJson),
            isCommandExecution);
    }

    private static TResponse? DeserializePayload<TResponse>(string payloadJson)
        where TResponse : class
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(payloadJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildApprovalStatus(AiProposedAction? action, AiApprovalRequest? approval)
    {
        if (approval?.Decision is not null)
        {
            return approval.Decision.DecisionType switch
            {
                AiApprovalDecisionType.ApproveOnce => "Approved once",
                AiApprovalDecisionType.Approve when approval.Decision.AdminOverrideUsed => "Approved with admin override",
                AiApprovalDecisionType.Approve => "Approved",
                AiApprovalDecisionType.Deny => "Denied",
                _ => approval.Decision.State.ToString()
            };
        }

        if (approval is not null)
        {
            return approval.State switch
            {
                AiApprovalState.Pending => "Pending approval",
                AiApprovalState.Approved => "Approved",
                AiApprovalState.Denied => "Denied",
                AiApprovalState.Blocked => "Blocked",
                _ => approval.State.ToString()
            };
        }

        return action?.ApprovalRequirement switch
        {
            AiApprovalRequirement.AutoRun => "Auto-run",
            AiApprovalRequirement.UserConfirmation => "Approved",
            AiApprovalRequirement.AdminApproval => "Approved",
            AiApprovalRequirement.Blocked => "Blocked",
            _ => "Auto-run"
        };
    }

    private static string BuildToolTitle(string toolName) =>
        string.Join(' ', toolName
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();

    private static string BuildActiveSummary(AiToolInvocation invocation) => invocation.Status switch
    {
        AiInvocationStatus.Pending => $"{BuildToolTitle(invocation.ToolName)} is queued.",
        AiInvocationStatus.Running => $"{BuildToolTitle(invocation.ToolName)} is running.",
        AiInvocationStatus.Cancelled => $"{BuildToolTitle(invocation.ToolName)} was cancelled.",
        AiInvocationStatus.Failed => $"{BuildToolTitle(invocation.ToolName)} failed.",
        AiInvocationStatus.Succeeded => $"{BuildToolTitle(invocation.ToolName)} completed.",
        _ => $"{BuildToolTitle(invocation.ToolName)} is active."
    };

    private static AiExecutionOutcome MapOutcome(AiInvocationStatus status) => status switch
    {
        AiInvocationStatus.Succeeded => AiExecutionOutcome.Succeeded,
        AiInvocationStatus.Failed => AiExecutionOutcome.Failed,
        AiInvocationStatus.Cancelled => AiExecutionOutcome.Cancelled,
        _ => AiExecutionOutcome.Pending
    };

    private static string ResolveTargetServerName(
        string toolName,
        string argumentsJson,
        IReadOnlyDictionary<Guid, string> attachedServerNames)
    {
        var serverId = AiCommandDisplayFormatter.ResolveTargetServerId(toolName, argumentsJson);
        if (!serverId.HasValue)
        {
            return string.Empty;
        }

        return attachedServerNames.GetValueOrDefault(serverId.Value, serverId.Value.ToString());
    }

    private static string ResolveCommandText(string toolName, string argumentsJson, params string?[] candidates)
    {
        var preview = AiCommandDisplayFormatter.BuildCommandPreview(toolName, argumentsJson);
        if (!string.IsNullOrWhiteSpace(preview))
        {
            return preview;
        }

        foreach (var candidate in candidates)
        {
            var sanitized = AiCommandDisplayFormatter.SanitizeDisplayText(candidate ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                return sanitized;
            }
        }

        return string.Empty;
    }

    private static string PrettyJson(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return JsonSerializer.Serialize(document.RootElement, SerializerOptions);
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }

    private sealed record ToolDetails(
        string TargetServerName,
        string CommandText,
        string StandardOutput,
        string StandardError,
        string OutputText,
        string PayloadText,
        bool IsCommandExecution);
}
