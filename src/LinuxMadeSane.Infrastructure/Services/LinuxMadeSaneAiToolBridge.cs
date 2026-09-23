// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.HomeLab;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

// Guardrail: AI tool execution stays on shared host abstractions. This bridge should not
// grow its own SFTP, SSH credential, or local-command transport branches.
public sealed partial class LinuxMadeSaneAiToolBridge(
    IAiToolRegistry toolRegistry,
    IAiConversationStore conversationStore,
    IManagedHostStore hostStore,
    IAiSafeChangeService safeChangeService,
    ICommandExecutionService commandExecutionService,
    IManagedHostFileAccessService fileAccessService,
    IHomeLabService? homeLabService = null,
    IHomeLabPromptRecipeProvider? promptRecipeProvider = null) : IAiToolBridge
{
    public LinuxMadeSaneAiToolBridge(
        IAiToolRegistry toolRegistry,
        IAiConversationStore conversationStore,
        IManagedHostStore hostStore,
        ICommandExecutionService commandExecutionService,
        IManagedHostFileAccessService fileAccessService)
        : this(
            toolRegistry,
            conversationStore,
            hostStore,
            new NoOpAiSafeChangeService(),
            commandExecutionService,
            fileAccessService)
    {
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<AiToolDefinition> ListPublishedTools(
        AiChatThread thread,
        IReadOnlyList<AiAttachedServer> attachedServers) =>
        toolRegistry.ListPublishedTools(thread, attachedServers);

    public AiToolDefinition? FindTool(string toolName) =>
        toolRegistry.FindTool(toolName);

    public async Task<AiToolExecutionResult> InvokeAsync(
        AiToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var thread = await conversationStore.GetThreadAsync(invocation.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The AI chat thread for this tool invocation could not be found.");
        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            invocation.ThreadId,
            await conversationStore.ListAttachedServersAsync(invocation.ThreadId, cancellationToken));
        var context = new AiToolExecutionContext(invocation, thread, attachedServers);
        var definition = FindTool(invocation.ToolName)
            ?? throw new InvalidOperationException($"Tool {invocation.ToolName} is not registered.");

        return definition.Name switch
        {
            AiToolNames.ListServers => await ExecuteListServersAsync(definition, context, cancellationToken),
            AiToolNames.GetServerSummary => await ExecuteGetServerSummaryAsync(definition, context, cancellationToken),
            AiToolNames.GetServerHealth => await ExecuteGetServerHealthAsync(definition, context, cancellationToken),
            AiToolNames.ListServices => await ExecuteListServicesAsync(definition, context, cancellationToken),
            AiToolNames.RestartService => await ExecuteRestartServiceAsync(definition, context, cancellationToken),
            AiToolNames.BrowseDirectory => await ExecuteBrowseDirectoryAsync(definition, context, cancellationToken),
            AiToolNames.ReadFile => await ExecuteReadFileAsync(definition, context, cancellationToken),
            AiToolNames.RunCommand => await ExecuteRunCommandAsync(definition, context, cancellationToken),
            AiToolNames.WriteFileWithConfirmation => await ExecuteWriteFileAsync(definition, context, cancellationToken),
            AiToolNames.InstallPackageWithConfirmation => await ExecuteInstallPackageAsync(definition, context, cancellationToken),
            AiToolNames.InspectHomeLab => await ExecuteInspectHomeLabAsync(definition, context, cancellationToken),
            AiToolNames.InspectHomeLabApplicationConfig => await ExecuteInspectHomeLabApplicationConfigAsync(definition, context, cancellationToken),
            AiToolNames.RepairHomeLabApplicationConfig => await ExecuteRepairHomeLabApplicationConfigAsync(definition, context, cancellationToken),
            AiToolNames.RepairHomeLabInstallation => await ExecuteRepairHomeLabInstallationAsync(definition, context, cancellationToken),
            AiToolNames.ApplyHomeLabPromptRecipe => await ExecuteApplyHomeLabPromptRecipeAsync(definition, context, cancellationToken),
            AiToolNames.RollbackSafeChange => await safeChangeService.ExecuteRollbackAsync(thread, invocation, cancellationToken),
            _ => throw new InvalidOperationException($"Tool {definition.Name} is not supported by this bridge.")
        };
    }

    private async Task<AiToolExecutionResult> ExecuteListServersAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<ListServersToolRequest>(context.Invocation.ArgumentsJson);
        var hosts = await hostStore.ListAsync(cancellationToken);
        var attachedServerIds = context.AttachedServers
            .Select(server => server.ManagedHostId)
            .ToHashSet();
        var allKnownHosts = context.AttachedServers.Any(server => AiLocalMachine.IsLocalMachine(server.ManagedHostId))
            ? hosts.Prepend(AiLocalMachine.CreateManagedHost()).ToArray()
            : hosts.ToArray();

        var selectedHosts = context.AttachedServers.Count > 0 && !request.IncludeUnattachedServers
            ? allKnownHosts.Where(host => attachedServerIds.Contains(host.Id)).ToArray()
            : allKnownHosts.ToArray();

        var response = new ListServersToolResponse(
            selectedHosts
                .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
                .Select(host => new AiToolServerListItem(
                    host.Id,
                    host.Name,
                    host.Hostname,
                    host.Port,
                    host.Environment,
                    host.Platform,
                    host.OperatingStatus,
                    host.LastConnectionTestStatus,
                    attachedServerIds.Contains(host.Id)))
                .ToArray(),
            context.AttachedServers.Count);

        var summary = response.Servers.Count == 0
            ? "No Linux servers are available."
            : $"Returned {response.Servers.Count} server(s).";

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            summary,
            BuildListServersOutput(response),
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteGetServerSummaryAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<GetServerSummaryToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);

        var response = new GetServerSummaryToolResponse(
            new AiToolServerSummary(
                host.Id,
                host.Name,
                host.Hostname,
                host.Port,
                host.Environment,
                host.Description,
                host.DefaultWorkingDirectory,
                host.Platform,
                host.OperatingStatus,
                host.LastConnectionTestStatus,
                host.LastSeenUtc,
                !string.IsNullOrWhiteSpace(host.PasswordSecretReference),
                !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference),
                host.UseKeyboardInteractiveFallback));

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Loaded server summary for {host.Name}.",
            BuildServerSummaryOutput(response.Server),
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteGetServerHealthAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<GetServerHealthToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);

        var command = WrapShellScript("""
            printf 'host\t'; hostname
            printf 'uptime\t'; (uptime -p 2>/dev/null || uptime)
            printf 'load\t'; cat /proc/loadavg 2>/dev/null
            printf 'memory_mb\t'; free -m | awk '/Mem:/ {print $2 " " $3 " " $4}'
            printf 'root_disk\t'; df -Pk / | awk 'NR==2 {print $2 " " $3 " " $4 " " $5}'
            """);

        var result = await ExecuteCommandAsync(host, command, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                ? $"Health collection failed on {host.Name} with exit code {result.ExitCode}."
                : result.StandardError.Trim());
        }

        var summary = BuildHealthSummary(result.StandardOutput);
        var response = new GetServerHealthToolResponse(
            host.Id,
            host.Name,
            host.Platform,
            host.OperatingStatus,
            host.LastConnectionTestStatus,
            host.Hostname,
            host.Port,
            summary,
            result.StandardOutput,
            result.CompletedAtUtc);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Collected live health from {host.Name}.",
            $"{summary}{Environment.NewLine}{Environment.NewLine}{result.StandardOutput.Trim()}",
            result.StandardError,
            result.ExitCode);
    }

    private async Task<AiToolExecutionResult> ExecuteListServicesAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<ListServicesToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);

        const string baseCommand = "systemctl list-units --type=service --all --plain --no-pager --no-legend";
        var script = string.IsNullOrWhiteSpace(request.Filter)
            ? baseCommand
            : $"output=\"$({baseCommand})\"; status=$?; if [ $status -ne 0 ]; then exit $status; fi; printf '%s\\n' \"$output\" | grep -i -- {QuoteShellArgument(request.Filter.Trim())} || true";
        var result = await ExecuteCommandAsync(host, WrapShellScript(script), cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                ? $"Service listing failed on {host.Name} with exit code {result.ExitCode}."
                : result.StandardError.Trim());
        }

        var response = new ListServicesToolResponse(
            host.Id,
            host.Name,
            ParseServiceList(result.StandardOutput),
            result.StandardOutput);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Listed {response.Services.Count} service(s) on {host.Name}.",
            BuildServiceListOutput(response),
            result.StandardError,
            result.ExitCode);
    }

    private async Task<AiToolExecutionResult> ExecuteRestartServiceAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RestartServiceToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);
        var command = WrapShellScript(
            $"sudo systemctl restart {QuoteShellArgument(request.ServiceName.Trim())} && systemctl is-active {QuoteShellArgument(request.ServiceName.Trim())}");
        var result = await ExecuteCommandAsync(host, command, cancellationToken);

        var response = new RestartServiceToolResponse(
            host.Id,
            host.Name,
            request.ServiceName.Trim(),
            result.IsSuccess,
            command,
            result.StandardOutput,
            result.StandardError,
            result.ExitCode,
            result.CompletedAtUtc);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            result.IsSuccess ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
            result.IsSuccess
                ? $"Restarted {response.ServiceName} on {host.Name}."
                : $"Restart failed for {response.ServiceName} on {host.Name}.",
            BuildCommandOutput(result),
            result.StandardError,
            result.ExitCode);
    }

    private async Task<AiToolExecutionResult> ExecuteBrowseDirectoryAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<BrowseDirectoryToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);
        var items = await ListDirectoryItemsAsync(host, request.Path, cancellationToken);

        var response = new BrowseDirectoryToolResponse(
            host.Id,
            host.Name,
            string.IsNullOrWhiteSpace(request.Path) ? host.DefaultWorkingDirectory : request.Path.Trim(),
            items
                .Select(item => new AiToolDirectoryItem(
                    item.Name,
                    item.FullPath,
                    item.ItemType,
                    item.SizeBytes,
                    item.LastModifiedUtc,
                    item.Permissions,
                    item.OwnerName,
                    item.GroupName,
                    item.PermissionsOctal,
                    item.LinkTarget))
                .ToArray());

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Listed {response.Items.Count} item(s) in {response.Path} on {host.Name}.",
            BuildDirectoryListingOutput(response),
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteReadFileAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<ReadFileToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);
        var content = await ReadFileAsync(host, request.Path, request.MaxBytes, cancellationToken);

        var response = new ReadFileToolResponse(
            host.Id,
            host.Name,
            content.FullPath,
            content.Content,
            content.SizeBytes,
            content.IsTruncated,
            content.LastModifiedUtc);

        var summary = content.IsTruncated
            ? $"Read the first {response.Content.Length} byte(s) from {response.Path} on {host.Name}."
            : $"Read {response.SizeBytes} byte(s) from {response.Path} on {host.Name}.";

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            summary,
            response.Content,
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteRunCommandAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RunCommandToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);

        var requestedCommandText = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? request.CommandText.Trim()
            : $"cd {QuoteShellArgument(request.WorkingDirectory.Trim())} && {request.CommandText.Trim()}";
        var commandText = $"sudo -n /bin/sh -lc {QuoteShellArgument(requestedCommandText)}";
        var result = await ExecuteCommandAsync(host, commandText, cancellationToken);

        var response = new RunCommandToolResponse(
            host.Id,
            host.Name,
            commandText,
            result.IsSuccess,
            result.StandardOutput,
            result.StandardError,
            result.ExitCode,
            result.StartedAtUtc,
            result.CompletedAtUtc);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            result.IsSuccess ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
            result.IsSuccess
                ? $"Command completed on {host.Name}."
                : $"Command failed on {host.Name}.",
            BuildCommandOutput(result),
            result.StandardError,
            result.ExitCode);
    }

    private async Task<AiToolExecutionResult> ExecuteWriteFileAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<WriteFileWithConfirmationToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);
        var writeResult = await WriteFileAsync(host, request.Path, request.Content, request.CreateDirectories, cancellationToken);

        var response = new WriteFileWithConfirmationToolResponse(
            host.Id,
            host.Name,
            writeResult.FullPath,
            writeResult.BytesWritten,
            "sftp-upload",
            writeResult.CompletedAtUtc);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Wrote {response.BytesWritten} byte(s) to {response.Path} on {host.Name}.",
            $"Path: {response.Path}{Environment.NewLine}Bytes written: {response.BytesWritten}{Environment.NewLine}Mode: {response.Mode}",
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteInstallPackageAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<InstallPackageWithConfirmationToolRequest>(context.Invocation.ArgumentsJson);
        var host = await ResolveAuthorizedHostAsync(request.ServerId, context.AttachedServers, cancellationToken);
        var packageNames = NormalizePackageNames(request.PackageNames);
        var packageArguments = string.Join(' ', packageNames.Select(QuoteShellArgument));
        var command = WrapShellScript($"sudo apt-get update && sudo apt-get install -y -- {packageArguments}");
        var result = await ExecuteCommandAsync(host, command, cancellationToken);

        var response = new InstallPackageWithConfirmationToolResponse(
            host.Id,
            host.Name,
            packageNames,
            result.IsSuccess,
            command,
            result.StandardOutput,
            result.StandardError,
            result.ExitCode,
            result.StartedAtUtc,
            result.CompletedAtUtc);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            result.IsSuccess ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
            result.IsSuccess
                ? $"Installed {packageNames.Count} package(s) on {host.Name}."
                : $"Package installation failed on {host.Name}.",
            BuildCommandOutput(result),
            result.StandardError,
            result.ExitCode);
    }

    private async Task<AiToolExecutionResult> ExecuteInspectHomeLabAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = DeserializeRequest<InspectHomeLabToolRequest>(context.Invocation.ArgumentsJson);
        var service = RequireHomeLabService();
        var workspace = await service.GetWorkspaceAsync(cancellationToken);
        var installations = new List<HomeLabAiInstallation>();
        foreach (var installation in workspace.Installations)
        {
            installations.Add(await MapHomeLabInstallationAsync(service, installation, cancellationToken));
        }

        var promptRecipes = await GetPromptRecipesAsync(cancellationToken);
        var response = new InspectHomeLabToolResponse(
            workspace.Installations
                .Where(item => item.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
                .Select(item => new HomeLabAiGateway(item.Id, item.DisplayName, item.HealthState.ToString(), item.HealthDetail))
                .ToArray(),
            installations,
            promptRecipes
                .Select(recipe => new HomeLabAiPromptRecipe(
                    recipe.Id,
                    recipe.Name,
                    recipe.Description,
                    recipe.AppIds,
                    recipe.RequiresVpnGateway))
                .ToArray());
        var output = BuildHomeLabInspectionOutput(response);

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Inspected {response.Installations.Count} LMS Home Lab installation(s).",
            output,
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteRepairHomeLabInstallationAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RepairHomeLabInstallationToolRequest>(context.Invocation.ArgumentsJson);
        var service = RequireHomeLabService();
        await service.ExecuteAsync(request.InstallationId, HomeLabLifecycleAction.RefreshHealth, cancellationToken);
        var installation = (await service.GetWorkspaceAsync(cancellationToken)).Installations
            .FirstOrDefault(item => item.Id == request.InstallationId)
            ?? throw new InvalidOperationException("The requested Home Lab Docker installation no longer exists.");
        var before = await MapHomeLabInstallationAsync(service, installation, cancellationToken);
        var workspace = await service.GetWorkspaceAsync(cancellationToken);
        var repairTarget = installation;
        HomeLabAppInstallation? routedGateway = null;
        if (installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            var gatewayContainerName = installation.NetworkMode["container:".Length..];
            routedGateway = workspace.Installations.FirstOrDefault(item =>
                item.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase) &&
                item.ContainerName.Equals(gatewayContainerName, StringComparison.OrdinalIgnoreCase));
        }
        if (!request.RestoreContainerSettings && installation.HealthState is HomeLabHealthState.Healthy or HomeLabHealthState.Starting)
        {
            var guardedResponse = new RepairHomeLabInstallationToolResponse(
                installation.Id,
                installation.DisplayName,
                "No container repair performed",
                true,
                before,
                before,
                installation.HealthState == HomeLabHealthState.Healthy
                    ? "LMS refused to recreate a healthy container. Inspect the app's persistent configuration and local access before proposing a scoped configuration repair."
                    : "LMS refused to recreate a container whose health check is still starting. Inspect it again after startup completes.",
                DateTimeOffset.UtcNow);
            return CreateExecutionResult(
                definition,
                context.Invocation,
                guardedResponse,
                AiExecutionOutcome.Succeeded,
                $"Protected healthy Home Lab container {installation.DisplayName} from an unnecessary rebuild.",
                guardedResponse.Detail,
                string.Empty,
                0);
        }
        var action = installation.HealthState switch
        {
            HomeLabHealthState.Stopped => HomeLabLifecycleAction.Start,
            HomeLabHealthState.Degraded or HomeLabHealthState.Failed or HomeLabHealthState.Blocked => HomeLabLifecycleAction.Repair,
            _ => HomeLabLifecycleAction.RefreshHealth
        };
        var actionLabel = action.ToString();
        HomeLabOperationResult operation;
        if (request.RestoreContainerSettings)
        {
            actionLabel = "Restore pre-edit container settings";
            operation = await service.RestoreContainerSettingsAsync(request.InstallationId, cancellationToken);
        }
        else if (installation.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
        {
            action = HomeLabLifecycleAction.Repair;
            actionLabel = "Repair VPN gateway namespace";
            operation = await service.ExecuteAsync(repairTarget.Id, action, cancellationToken);
        }
        else if (routedGateway?.HealthState is HomeLabHealthState.Degraded or HomeLabHealthState.Failed or HomeLabHealthState.Stopped or HomeLabHealthState.Blocked)
        {
            repairTarget = routedGateway;
            action = HomeLabLifecycleAction.Repair;
            actionLabel = "Repair shared VPN gateway namespace";
            operation = await service.ExecuteAsync(repairTarget.Id, action, cancellationToken);
        }
        else
        {
            operation = await service.ExecuteAsync(repairTarget.Id, action, cancellationToken);
        }

        var refreshed = await WaitForHomeLabInstallationAsync(service, installation.Id, cancellationToken);
        var after = await MapHomeLabInstallationAsync(service, refreshed, cancellationToken);
        var app = HomeLabCatalog.GetApp(refreshed.AppId);
        var networkReady = !after.IsVpnRouted || after.IsSecured;
        var succeeded = operation.Succeeded &&
                        refreshed.HealthState == HomeLabHealthState.Healthy &&
                        networkReady &&
                        (!app.RequiresVpnGateway || after.IsSecured);
        var response = new RepairHomeLabInstallationToolResponse(
            refreshed.Id,
            refreshed.DisplayName,
            actionLabel,
            succeeded,
            before,
            after,
            $"{operation.Summary} {operation.Detail}",
            DateTimeOffset.UtcNow);
        var output = string.Join(
            Environment.NewLine,
            $"Before: {FormatHomeLabInstallation(before)}",
            $"LMS action: {actionLabel} via {repairTarget.DisplayName} ({repairTarget.Id})",
            response.Detail,
            $"After: {FormatHomeLabInstallation(after)}");

        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            succeeded ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
            succeeded
                ? $"Repaired and verified Home Lab container {refreshed.DisplayName}."
                : $"Home Lab container {refreshed.DisplayName} still needs attention.",
            output,
            succeeded ? string.Empty : "The container did not reach a healthy LMS-managed state after the repair attempt.",
            succeeded ? 0 : 1);
    }

    private async Task<AiToolExecutionResult> ExecuteInspectHomeLabApplicationConfigAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<InspectHomeLabApplicationConfigToolRequest>(context.Invocation.ArgumentsJson);
        var inspection = await RequireHomeLabService().InspectApplicationConfigAsync(
            request.InstallationId,
            request.RelativePath,
            cancellationToken);
        var response = new InspectHomeLabApplicationConfigToolResponse(
            inspection.InstallationId,
            inspection.Name,
            inspection.Files,
            inspection.Detail);
        var output = inspection.Files.Count == 0
            ? inspection.Detail
            : string.Join(Environment.NewLine + Environment.NewLine, inspection.Files.Select(file =>
                $"File: {file.RelativePath}{Environment.NewLine}SHA-256: {file.Sha256}{Environment.NewLine}{file.RedactedContent}"));
        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            AiExecutionOutcome.Succeeded,
            $"Inspected {inspection.Files.Count} safe configuration file(s) for {inspection.Name}.",
            output,
            string.Empty,
            0);
    }

    private async Task<AiToolExecutionResult> ExecuteRepairHomeLabApplicationConfigAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RepairHomeLabApplicationConfigToolRequest>(context.Invocation.ArgumentsJson);
        var service = RequireHomeLabService();
        var result = await service.RepairApplicationConfigAsync(
            request.InstallationId,
            new HomeLabApplicationConfigPatch(request.RelativePath, request.ExpectedSha256, request.ExpectedText, request.ReplacementText),
            cancellationToken);
        var response = new RepairHomeLabApplicationConfigToolResponse(request.InstallationId, result.Name, result);
        return CreateExecutionResult(
            definition,
            context.Invocation,
            response,
            result.Succeeded ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
            result.Succeeded
                ? $"Repaired and verified {result.Name} application configuration."
                : $"The {result.Name} configuration repair was not retained.",
            $"File: {result.RelativePath}{Environment.NewLine}Backup: {result.BackupRelativePath}{Environment.NewLine}{result.Detail}",
            result.Succeeded ? string.Empty : result.Detail,
            result.Succeeded ? 0 : 1);
    }

    private async Task<AiToolExecutionResult> ExecuteApplyHomeLabPromptRecipeAsync(
        AiToolDefinition definition,
        AiToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<ApplyHomeLabPromptRecipeToolRequest>(context.Invocation.ArgumentsJson);
        var recipe = await GetPromptRecipeAsync(request.PromptRecipeId, cancellationToken);
        var service = RequireHomeLabService();
        var workspace = await service.GetWorkspaceAsync(cancellationToken);
        var details = new List<string>();
        HomeLabAppInstallation? gateway = null;

        var missingStorageRoles = recipe.AppIds
            .Where(appId => workspace.Installations.All(installation =>
                !installation.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(appId => HomeLabCatalog.GetApp(appId).Volumes)
            .Select(volume => volume.SharedRole)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(role => workspace.StorageRoles.All(saved =>
                !saved.Role.Equals(role, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(saved.HostPath)))
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingStorageRoles.Length > 0)
        {
            var roles = string.Join(", ", missingStorageRoles.Select(role => $"'{role}'"));
            return CreateHomeLabApplyResult(
                definition,
                context.Invocation,
                recipe,
                false,
                [],
                [$"Before installation, choose a host path for shared storage role(s) {roles} in Home Lab > Storage."],
                missingStorageRoles);
        }

        if (recipe.RequiresVpnGateway)
        {
            var gateways = workspace.Installations
                .Where(item => item.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            gateway = request.VpnGatewayInstallationId.HasValue
                ? gateways.FirstOrDefault(item => item.Id == request.VpnGatewayInstallationId.Value)
                : gateways.Length == 1
                    ? gateways[0]
                    : null;

            if (gateway is null)
            {
                var reason = gateways.Length == 0
                    ? "No VPN Gateway is installed. Configure one in Home Lab > Apps so credentials remain in LMS secret fields."
                    : "Several VPN Gateways are installed. Inspect Home Lab and choose one by installation ID.";
                return CreateHomeLabApplyResult(definition, context.Invocation, recipe, false, [], [reason]);
            }

            if (gateway.HealthState == HomeLabHealthState.Stopped)
            {
                var start = await service.ExecuteAsync(gateway.Id, HomeLabLifecycleAction.Start, cancellationToken);
                details.Add($"VPN Gateway: {start.Summary} {start.Detail}");
            }
            else if (gateway.HealthState is HomeLabHealthState.Degraded or HomeLabHealthState.Failed or HomeLabHealthState.Blocked)
            {
                var repair = await service.ExecuteAsync(gateway.Id, HomeLabLifecycleAction.Repair, cancellationToken);
                details.Add($"VPN Gateway: {repair.Summary} {repair.Detail}");
            }
        }

        var targetInstallations = new List<HomeLabAiInstallation>();
        var succeeded = true;
        foreach (var appId in recipe.AppIds)
        {
            workspace = await service.GetWorkspaceAsync(cancellationToken);
            var installation = workspace.Installations
                .Where(item => item.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();

            if (installation is null)
            {
                var configuration = recipe.RequiresVpnGateway
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["network-route"] = "VPN Gateway (Gluetun)",
                        ["vpn-gateway"] = gateway!.Id.ToString()
                    }
                    : null;
                var install = await service.InstallAppAsync(
                    new HomeLabInstallRequest(appId, Configuration: configuration),
                    cancellationToken);
                details.Add($"{HomeLabCatalog.GetApp(appId).Name}: {install.Summary} {install.Detail}");
                if (!install.Succeeded)
                {
                    succeeded = false;
                    continue;
                }

                workspace = await service.GetWorkspaceAsync(cancellationToken);
                installation = workspace.Installations
                    .Where(item => item.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .FirstOrDefault();
            }
            else
            {
                details.Add($"{installation.DisplayName}: reused existing LMS installation.");
            }

            if (installation is null)
            {
                succeeded = false;
                details.Add($"{HomeLabCatalog.GetApp(appId).Name}: LMS did not return the installation after deployment.");
                continue;
            }

            if (recipe.RequiresVpnGateway)
            {
                var route = await service.SetNetworkRouteAsync(installation.Id, true, gateway!.Id, cancellationToken);
                details.Add($"{installation.DisplayName}: {route.Summary} {route.Detail}");
                succeeded &= route.Succeeded;
            }

            if (installation.HealthState is HomeLabHealthState.Degraded or HomeLabHealthState.Failed or HomeLabHealthState.Blocked)
            {
                var repair = await service.ExecuteAsync(installation.Id, HomeLabLifecycleAction.Repair, cancellationToken);
                details.Add($"{installation.DisplayName}: {repair.Summary} {repair.Detail}");
                succeeded &= repair.Succeeded;
            }

            installation = await WaitForHomeLabInstallationAsync(service, installation.Id, cancellationToken);
            var mapped = await MapHomeLabInstallationAsync(service, installation, cancellationToken);
            targetInstallations.Add(mapped);
            succeeded &= installation.HealthState is HomeLabHealthState.Healthy or HomeLabHealthState.Degraded;
            succeeded &= !recipe.RequiresVpnGateway || mapped.IsSecured;
        }

        if (targetInstallations.Count > 0)
        {
            await service.AssignRecipeRunAsync(
                new HomeLabRecipeRunAssignment(
                    recipe.Id,
                    recipe.Name,
                    Guid.NewGuid(),
                    targetInstallations.Select(item => item.InstallationId).ToArray()),
                cancellationToken);
        }

        return CreateHomeLabApplyResult(
            definition,
            context.Invocation,
            recipe,
            succeeded && targetInstallations.Count == recipe.AppIds.Count,
            targetInstallations,
            details);
    }

    private static AiToolExecutionResult CreateHomeLabApplyResult(
        AiToolDefinition definition,
        AiToolInvocation invocation,
        HomeLabPromptRecipe recipe,
        bool succeeded,
        IReadOnlyList<HomeLabAiInstallation> installations,
        IReadOnlyList<string> details,
        IReadOnlyList<string>? requiredStorageRoles = null)
    {
        var response = new ApplyHomeLabPromptRecipeToolResponse(
            recipe.Id,
            recipe.Name,
            succeeded,
            installations,
            details,
            DateTimeOffset.UtcNow,
            requiredStorageRoles);
        return CreateExecutionResult(
            definition,
            invocation,
            response,
            succeeded ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
            succeeded
                ? $"Applied and verified Home Lab prompt recipe {recipe.Name}."
                : $"Home Lab prompt recipe {recipe.Name} needs attention.",
            string.Join(Environment.NewLine, details.Concat(installations.Select(FormatHomeLabInstallation))),
            succeeded ? string.Empty : "One or more requested apps did not reach a secured, usable LMS state.",
            succeeded ? 0 : 1);
    }

    private static async Task<HomeLabAppInstallation> WaitForHomeLabInstallationAsync(
        IHomeLabService service,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        HomeLabAppInstallation? installation = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await service.ExecuteAsync(installationId, HomeLabLifecycleAction.RefreshHealth, cancellationToken);
            installation = (await service.GetWorkspaceAsync(cancellationToken)).Installations
                .First(item => item.Id == installationId);
            if (installation.HealthState is HomeLabHealthState.Healthy or HomeLabHealthState.Degraded or
                HomeLabHealthState.Failed or HomeLabHealthState.Blocked or HomeLabHealthState.Stopped)
            {
                return installation;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return installation ?? throw new InvalidOperationException("The Home Lab installation disappeared while LMS was checking its health.");
    }

    private static async Task<HomeLabAiInstallation> MapHomeLabInstallationAsync(
        IHomeLabService service,
        HomeLabAppInstallation installation,
        CancellationToken cancellationToken)
    {
        var app = HomeLabCatalog.GetApp(installation.AppId);
        var isVpnRouted = installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase);
        var isSecured = false;
        HomeLabNetworkSecurity? security = null;
        if (app.SupportsVpnGateway)
        {
            try
            {
                security = await service.GetNetworkSecurityAsync(installation.Id, cancellationToken);
                isVpnRouted = security.IsVpnRouted;
                isSecured = security.IsSecured;
            }
            catch
            {
                isSecured = false;
            }
        }

        return new HomeLabAiInstallation(
            installation.Id,
            installation.DeploymentId,
            installation.AppId,
            installation.DisplayName,
            installation.ContainerName,
            installation.Image,
            installation.NetworkMode,
            app.Dependencies,
            installation.HealthState.ToString(),
            installation.HealthDetail,
            isVpnRouted,
            isSecured,
            installation.CaddySourcePort is int port ? $"http://<LMS host>:{port}" : string.Empty,
            ParseHomeLabAiPorts(installation.PortMappingsJson),
            security?.PortForwardingStatus ?? string.Empty,
            security?.ForwardedPort,
            security?.PortForwardingDetail ?? string.Empty);
    }

    private IHomeLabService RequireHomeLabService() =>
        homeLabService ?? throw new InvalidOperationException("LMS Home Lab services are unavailable to this AI session.");

    private Task<IReadOnlyList<HomeLabPromptRecipe>> GetPromptRecipesAsync(CancellationToken cancellationToken) =>
        promptRecipeProvider?.GetRecipesAsync(cancellationToken)
        ?? Task.FromResult(HomeLabPromptRecipeCatalog.All);

    private async Task<HomeLabPromptRecipe> GetPromptRecipeAsync(string id, CancellationToken cancellationToken) =>
        promptRecipeProvider is null
            ? HomeLabPromptRecipeCatalog.Get(id)
            : await promptRecipeProvider.GetRecipeAsync(id, cancellationToken);

    private static string BuildHomeLabInspectionOutput(InspectHomeLabToolResponse response)
    {
        var gateways = response.VpnGateways.Count == 0
            ? "VPN Gateways: none installed"
            : "VPN Gateways:" + Environment.NewLine + string.Join(Environment.NewLine, response.VpnGateways.Select(item =>
                $"- {item.Name} | {item.InstallationId} | {item.HealthState} | {item.HealthDetail}"));
        var installations = response.Installations.Count == 0
            ? "Installations: none"
            : "Installations:" + Environment.NewLine + string.Join(Environment.NewLine, response.Installations.Select(FormatHomeLabInstallation));
        return $"{gateways}{Environment.NewLine}{installations}";
    }

    private static string FormatHomeLabInstallation(HomeLabAiInstallation item) =>
        $"- {item.Name} ({item.AppId}) | installation={item.InstallationId} | container={item.ContainerName} | image={item.Image} | network={item.NetworkMode} | ports={FormatHomeLabPorts(item.Ports)} | dependencies={string.Join(",", item.Dependencies)} | {item.HealthState}: {item.HealthDetail} | VPN routed={item.IsVpnRouted} | secured={item.IsSecured} | forwarding={FormatHomeLabForwarding(item)} | access={item.AccessUrl}";

    private static IReadOnlyList<HomeLabAiPort> ParseHomeLabAiPorts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HomeLabAiPort>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FormatHomeLabPorts(IReadOnlyList<HomeLabAiPort> ports) =>
        ports.Count == 0
            ? "none"
            : string.Join(",", ports.Select(port => $"{port.Name}:{port.ContainerPort}->127.0.0.1:{port.HostPort}"));

    private static string FormatHomeLabForwarding(HomeLabAiInstallation item) =>
        string.IsNullOrWhiteSpace(item.PortForwardingStatus)
            ? "n/a"
            : $"{item.PortForwardingStatus} port={item.ForwardedPort?.ToString() ?? "none"} ({item.PortForwardingDetail})";

    private async Task<ManagedHost> ResolveAuthorizedHostAsync(
        Guid serverId,
        IReadOnlyList<AiAttachedServer> attachedServers,
        CancellationToken cancellationToken)
    {
        if (AiLocalMachine.IsLocalMachine(serverId))
        {
            if (attachedServers.All(server => server.ManagedHostId != serverId))
            {
                throw new InvalidOperationException("The local machine is not attached to this AI chat.");
            }

            return AiLocalMachine.CreateManagedHost();
        }

        var host = await hostStore.GetAsync(serverId, cancellationToken)
            ?? throw new InvalidOperationException("The requested server could not be found.");

        if (attachedServers.Count > 0 && attachedServers.All(server => server.ManagedHostId != serverId))
        {
            throw new InvalidOperationException($"Server {host.Name} is not attached to this AI chat.");
        }

        return host;
    }

    private async Task<CommandExecutionResult> ExecuteCommandAsync(
        ManagedHost host,
        string commandText,
        CancellationToken cancellationToken)
        => await commandExecutionService.ExecuteAsync(host, commandText, cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<SftpItem>> ListDirectoryItemsAsync(
        ManagedHost host,
        string path,
        CancellationToken cancellationToken)
        => await fileAccessService.ListItemsAsync(
            host,
            path,
            CreateStoredConnectionProfile(host),
            cancellationToken);

    private async Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
        => await fileAccessService.ReadFileAsync(
            host,
            path,
            CreateStoredConnectionProfile(host),
            maxBytes,
            cancellationToken);

    private async Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        bool createDirectories,
        CancellationToken cancellationToken)
        => await fileAccessService.WriteFileAsync(
            host,
            path,
            content,
            CreateStoredConnectionProfile(host),
            createDirectories,
            encodingName: null,
            cancellationToken);

    private static ManagedHostConnectionProfile CreateStoredConnectionProfile(ManagedHost host) =>
        new(host.Username, null, PreferStoredCredentials: true, UseSshTransport: false, UseSudo: true);

    private static TRequest DeserializeRequest<TRequest>(string argumentsJson)
        where TRequest : IAiToolRequest
    {
        var request = JsonSerializer.Deserialize<TRequest>(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            SerializerOptions);

        return request ?? throw new InvalidOperationException($"Failed to deserialize {typeof(TRequest).Name}.");
    }

    private static AiToolExecutionResult CreateExecutionResult(
        AiToolDefinition definition,
        AiToolInvocation invocation,
        IAiToolResponse response,
        AiExecutionOutcome outcome,
        string summary,
        string outputText,
        string errorText,
        int? exitCode)
    {
        var completedAtUtc = DateTimeOffset.UtcNow;
        var payloadJson = JsonSerializer.Serialize(response, response.GetType(), SerializerOptions);

        return new AiToolExecutionResult(
            definition,
            response,
            new AiToolResult(
                Guid.NewGuid(),
                invocation.Id,
                outcome,
                summary,
                outputText,
                errorText,
                payloadJson,
                exitCode,
                completedAtUtc));
    }

    private static string BuildListServersOutput(ListServersToolResponse response)
    {
        if (response.Servers.Count == 0)
        {
            return "No servers are available.";
        }

        var lines = response.Servers.Select(server =>
            $"{server.Name} | {FormatHostEndpoint(server.Hostname, server.Port)} | {server.OperatingStatus} | connection {server.ConnectionStatus} | attached={server.IsAttachedToThread}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildServerSummaryOutput(AiToolServerSummary summary) =>
        $"Name: {summary.Name}{Environment.NewLine}" +
        $"Host: {FormatHostEndpoint(summary.Hostname, summary.Port)}{Environment.NewLine}" +
        $"Platform: {summary.Platform}{Environment.NewLine}" +
        $"Working directory: {summary.DefaultWorkingDirectory}{Environment.NewLine}" +
        $"Operating status: {summary.OperatingStatus}{Environment.NewLine}" +
        $"Connection status: {summary.ConnectionStatus}{Environment.NewLine}" +
        $"Stored credentials: {summary.HasStoredPassword || summary.HasStoredPrivateKey}";

    private static string BuildHealthSummary(string rawOutput)
    {
        var lines = rawOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(
                line => line.Split('\t', 2)[0],
                line => line.Split('\t', 2).ElementAtOrDefault(1) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var uptime = lines.GetValueOrDefault("uptime", "unavailable");
        var load = lines.GetValueOrDefault("load", "unavailable");
        var memory = lines.GetValueOrDefault("memory_mb", "unavailable");
        var disk = lines.GetValueOrDefault("root_disk", "unavailable");
        return $"Uptime: {uptime}. Load: {load}. Memory MB (total used free): {memory}. Root disk (1K blocks used avail use%): {disk}.";
    }

    private static IReadOnlyList<AiToolServiceListItem> ParseServiceList(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseServiceLine)
            .ToArray();

    private static AiToolServiceListItem ParseServiceLine(string line)
    {
        var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
        {
            return new AiToolServiceListItem(line, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        return new AiToolServiceListItem(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    private static string BuildServiceListOutput(ListServicesToolResponse response)
    {
        if (response.Services.Count == 0)
        {
            return $"No services matched on {response.ServerName}.";
        }

        var lines = response.Services.Select(service =>
            $"{service.UnitName} | load={service.LoadState} | active={service.ActiveState} | sub={service.SubState} | {service.Description}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDirectoryListingOutput(BrowseDirectoryToolResponse response)
    {
        if (response.Items.Count == 0)
        {
            return $"No items found in {response.Path}.";
        }

        var lines = response.Items.Select(item =>
            $"{FormatDirectoryItemName(item)} | {item.ItemType} | {item.SizeBytes} bytes | {FormatDirectoryItemPermissions(item)} | {FormatDirectoryItemOwner(item)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDirectoryItemName(AiToolDirectoryItem item) =>
        item.ItemType == SftpItemType.Link && !string.IsNullOrWhiteSpace(item.LinkTarget)
            ? $"{item.Name} -> {item.LinkTarget}"
            : item.Name;

    private static string FormatDirectoryItemPermissions(AiToolDirectoryItem item) =>
        string.IsNullOrWhiteSpace(item.PermissionsOctal)
            ? item.Permissions
            : $"{item.Permissions} / {item.PermissionsOctal}";

    private static string FormatDirectoryItemOwner(AiToolDirectoryItem item)
    {
        var owner = string.IsNullOrWhiteSpace(item.OwnerName) ? "-" : item.OwnerName;
        var group = string.IsNullOrWhiteSpace(item.GroupName) ? "-" : item.GroupName;
        return $"{owner}:{group}";
    }

    private static string BuildCommandOutput(CommandExecutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.CommandText);
        builder.AppendLine($"Exit code: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine();
            builder.AppendLine(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine();
            builder.AppendLine("stderr:");
            builder.AppendLine(result.StandardError.TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    private static string WrapShellScript(string script) =>
        $"/bin/sh -lc {QuoteShellArgument(script.Trim())}";

    private static string FormatHostEndpoint(string hostname, int port) =>
        port > 0
            ? $"{hostname}:{port}"
            : hostname;

    private static IReadOnlyList<string> NormalizePackageNames(IReadOnlyList<string> packageNames)
    {
        var normalized = packageNames
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("At least one package name is required.");
        }

        foreach (var packageName in normalized)
        {
            if (!SafePackageNamePattern().IsMatch(packageName))
            {
                throw new InvalidOperationException($"Package name {packageName} is not valid for apt-get installation.");
            }
        }

        return normalized;
    }

    private static string QuoteShellArgument(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    [GeneratedRegex("^[A-Za-z0-9.+:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePackageNamePattern();
}
