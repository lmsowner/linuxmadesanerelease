// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
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
    IManagedHostFileAccessService fileAccessService) : IAiToolBridge
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

        var commandText = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? request.CommandText.Trim()
            : $"cd {QuoteShellArgument(request.WorkingDirectory.Trim())} && {request.CommandText.Trim()}";
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
    {
        if (!AiLocalMachine.IsLocalMachine(host.Id))
        {
            return await fileAccessService.ListItemsAsync(
                host,
                path,
                CreateStoredConnectionProfile(host),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
        var directory = new DirectoryInfo(normalizedPath);
        if (!directory.Exists)
        {
            throw new InvalidOperationException($"Directory {normalizedPath} was not found on the local machine.");
        }

        return directory
            .EnumerateFileSystemInfos()
            .Select(LocalFileBrowsingSupport.MapItem)
            .OrderByDescending(item => item.ItemType == SftpItemType.Folder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (!AiLocalMachine.IsLocalMachine(host.Id))
        {
            return await fileAccessService.ReadFileAsync(
                host,
                path,
                CreateStoredConnectionProfile(host),
                maxBytes,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
        if (Directory.Exists(normalizedPath))
        {
            throw new InvalidOperationException("The requested path is a directory, not a file.");
        }

        var fileInfo = new FileInfo(normalizedPath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException($"File {normalizedPath} was not found on the local machine.");
        }

        var safeMaxBytes = Math.Clamp(maxBytes, 1, 1_048_576);
        await using var stream = fileInfo.OpenRead();
        var buffer = new byte[safeMaxBytes];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        var decoded = TextFileEncoding.Decode(buffer.AsSpan(0, bytesRead));

        return new SftpFileContent(
            normalizedPath,
            decoded.Content,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            fileInfo.Length > bytesRead,
            decoded.EncodingName);
    }

    private async Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        bool createDirectories,
        CancellationToken cancellationToken)
    {
        if (!AiLocalMachine.IsLocalMachine(host.Id))
        {
            return await fileAccessService.WriteFileAsync(
                host,
                path,
                content,
                CreateStoredConnectionProfile(host),
                createDirectories,
                encodingName: null,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (createDirectories)
            {
                Directory.CreateDirectory(directory);
            }
            else if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Directory {directory} does not exist on the local machine.");
            }
        }

        await File.WriteAllTextAsync(normalizedPath, content, cancellationToken);

        return new SftpWriteResult(normalizedPath, Encoding.UTF8.GetByteCount(content), DateTimeOffset.UtcNow);
    }

    private static ManagedHostConnectionProfile CreateStoredConnectionProfile(ManagedHost host) =>
        new(host.Username, null, PreferStoredCredentials: true);

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
