// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

// Guardrail: runbook library/editing/execution lives behind IRunbookService.
// Host CRUD/discovery/health code must not grow new runbook behavior.
public sealed class RunbookService(
    IManagedHostStore hostStore,
    ISavedCommandStore savedCommandStore,
    ICommandExecutionService commandExecutionService) : IRunbookService
{
    private static readonly IReadOnlyList<StarterRunbookTemplate> StarterRunbooks =
    [
        new("Update apt indexes", "apt-get update", "Refresh package indexes before installs or upgrades.", true),
        new("Upgrade installed packages", "apt-get upgrade -y", "Apply the latest available package upgrades.", true),
        new("Check disk usage", "df -h", "Show filesystem usage in a readable format.", false),
        new("Find largest folders", "du -xh / | sort -h | tail -n 25", "Find the heaviest directories on the machine.", true),
        new("Show failed services", "systemctl --failed", "List systemd units that are currently failed.", true),
        new("Restart a service", "systemctl restart <service>", "Restart a named systemd unit after a config or code change.", true),
        new("Tail auth log", "tail -n 150 /var/log/auth.log", "Review recent authentication events.", true),
        new("Check recent journal errors", "journalctl -p err -n 150", "Show recent high-severity journal entries.", true),
        new("Check Docker containers", "docker ps -a", "Inspect current Docker container state.", true),
        new("Fix ownership on a share path", "chown -R <user>:<group> /srv/shares/<share>", "Reset ownership on a share path when permissions drift.", true)
    ];

    public async Task<IReadOnlyList<CommandLibraryItem>> ListCommandsAsync(CancellationToken cancellationToken = default)
    {
        var hosts = await hostStore.ListAsync(cancellationToken);
        var commands = await savedCommandStore.ListAsync(cancellationToken);
        var hostsById = hosts.ToDictionary(host => host.Id);
        return commands
            .GroupBy(BuildRunbookLibraryGroupKey)
            .Select(group =>
            {
                var primary = group
                    .OrderBy(command => AiLocalMachine.IsLocalMachine(command.HostId) ? 0 : 1)
                    .ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                    .First();
                var hostIds = group
                    .Select(command => command.HostId)
                    .Distinct()
                    .ToArray();
                var hostNames = hostIds
                    .Select(hostId => hostsById.TryGetValue(hostId, out var host) ? host.Name : "Unknown host")
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new CommandLibraryItem(
                    primary.Id,
                    primary.HostId,
                    BuildHostSummary(hostNames),
                    hostIds,
                    hostNames,
                    primary.Name,
                    primary.CommandText,
                    primary.Description,
                    primary.RequiresSudo,
                    primary.IsQuickAccess,
                    primary.IsTemplate,
                    primary.TemplateSourceId,
                    primary.LinkGroupId,
                    primary.ParameterDefinitions,
                    primary.ParameterValueSnapshot,
                    primary.IsGlobalFavorite);
            })
            .OrderBy(item => item.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RunbookExecutionResultViewModel> RunRunbookAsync(
        Guid runbookId,
        IProgress<RunbookExecutionProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var commands = await savedCommandStore.ListAsync(cancellationToken);
        var primary = commands.FirstOrDefault(command => command.Id == runbookId);
        if (primary is null)
        {
            throw new InvalidOperationException("Runbook not found.");
        }

        if (primary.IsTemplate)
        {
            throw new InvalidOperationException("Templates cannot be run directly. Create a runnable runbook from the template first.");
        }

        var targetCommands = ResolveLogicalRunbookGroup(commands, primary);
        var hosts = await hostStore.ListAsync(cancellationToken);
        var hostsById = hosts.ToDictionary(host => host.Id);

        foreach (var command in targetCommands)
        {
            var queuedHost = ResolveRunbookHost(command.HostId, hostsById);
            progress?.Report(new RunbookExecutionProgressUpdate(
                primary.Id,
                command.HostId,
                queuedHost?.Name ?? "Unknown host",
                RunbookExecutionProgressKind.Queued,
                "Queued for execution.",
                string.Empty,
                null,
                false,
                DateTimeOffset.UtcNow));
        }

        var results = await Task.WhenAll(targetCommands.Select(command => ExecuteRunbookAsync(primary.Id, command, hostsById, progress, cancellationToken)));
        return new RunbookExecutionResultViewModel(
            primary.Id,
            primary.Name,
            results
                .OrderBy(result => result.HostName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<Guid> SaveRunbookAsync(RunbookEditor editor, CancellationToken cancellationToken = default)
    {
        var hosts = await hostStore.ListAsync(cancellationToken);
        var hostsById = hosts.ToDictionary(host => host.Id);
        var normalizedName = editor.Name.Trim();
        var normalizedDescription = editor.Description.Trim();
        var normalizedDefinitions = RunbookTemplateRenderer.NormalizeDefinitions(editor.Parameters);
        var normalizedParameterValues = RunbookTemplateRenderer.NormalizeValueSnapshot(editor.Parameters);
        var selectedHostIds = NormalizeSelectedHostIds(editor, hostsById);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Runbook title is required.");
        }

        if (editor.IsTemplate)
        {
            if (string.IsNullOrWhiteSpace(editor.CommandText))
            {
                throw new InvalidOperationException("Runbook template content is required.");
            }

            var missingTokens = RunbookTemplateRenderer.FindMissingTokens(editor.CommandText, normalizedDefinitions);
            if (missingTokens.Count > 0)
            {
                throw new InvalidOperationException($"Define parameters for every token first. Missing: {string.Join(", ", missingTokens.Select(name => RunbookTemplateRenderer.BuildToken(name)))}.");
            }

            var templateHostId = selectedHostIds[0];
            var templateId = editor.Id ?? Guid.NewGuid();
            await savedCommandStore.SaveAsync(
                new SavedCommand(
                    templateId,
                    templateHostId,
                    normalizedName,
                    NormalizeRunbookContent(editor.CommandText),
                    normalizedDescription,
                    editor.RequiresSudo,
                    false,
                    true,
                    null,
                    null,
                    normalizedDefinitions,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    false),
                cancellationToken);

            return templateId;
        }

        var normalizedCommandText = editor.TemplateSourceId.HasValue && normalizedDefinitions.Count > 0
            ? NormalizeRunbookContent(RunbookTemplateRenderer.Render(editor.CommandText, normalizedDefinitions, normalizedParameterValues))
            : NormalizeRunbookContent(editor.CommandText);
        if (string.IsNullOrWhiteSpace(normalizedCommandText))
        {
            throw new InvalidOperationException("Runbook content is required.");
        }

        var allCommands = await savedCommandStore.ListAsync(cancellationToken);
        var existingCommand = editor.Id.HasValue
            ? allCommands.FirstOrDefault(command => command.Id == editor.Id.Value)
            : null;
        var existingRunbookGroup = existingCommand is null
            ? []
            : ResolveLogicalRunbookGroup(allCommands, existingCommand);
        var parameterDefinitionsForSave = editor.TemplateSourceId.HasValue ? normalizedDefinitions : [];
        var parameterValuesForSave = editor.TemplateSourceId.HasValue
            ? normalizedParameterValues
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var primaryRunbookId = editor.Id ?? Guid.NewGuid();
        var primaryHostId = selectedHostIds.Contains(editor.HostId)
            ? editor.HostId
            : selectedHostIds[0];

        await SaveRunnableRunbookGroupAsync(
            existingRunbookGroup,
            primaryRunbookId,
            primaryHostId,
            selectedHostIds,
            normalizedName,
            normalizedCommandText,
            normalizedDescription,
            editor.RequiresSudo,
            editor.IsQuickAccess,
            editor.IsGlobalFavorite,
            editor.TemplateSourceId,
            parameterDefinitionsForSave,
            parameterValuesForSave,
            cancellationToken);

        return primaryRunbookId;
    }

    public async Task DeleteRunbookAsync(Guid runbookId, CancellationToken cancellationToken = default)
    {
        var allCommands = await savedCommandStore.ListAsync(cancellationToken);
        var existing = allCommands.FirstOrDefault(command => command.Id == runbookId);
        if (existing is null)
        {
            throw new InvalidOperationException("Runbook not found.");
        }

        foreach (var command in ResolveLogicalRunbookGroup(allCommands, existing))
        {
            await savedCommandStore.DeleteAsync(command.Id, cancellationToken);
        }
    }

    public async Task SetRunbookHostsAsync(Guid runbookId, IReadOnlyList<Guid> hostIds, CancellationToken cancellationToken = default)
    {
        var normalizedHostIds = hostIds
            .Where(hostId => hostId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (normalizedHostIds.Length == 0)
        {
            throw new InvalidOperationException("Select at least one machine for this runbook.");
        }

        var hosts = await hostStore.ListAsync(cancellationToken);
        var hostsById = hosts.ToDictionary(host => host.Id);
        foreach (var hostId in normalizedHostIds)
        {
            if (!hostsById.ContainsKey(hostId))
            {
                throw new InvalidOperationException("Pick valid machines for this runbook.");
            }
        }

        var allCommands = await savedCommandStore.ListAsync(cancellationToken);
        var existing = allCommands.FirstOrDefault(command => command.Id == runbookId);
        if (existing is null)
        {
            throw new InvalidOperationException("Runbook not found.");
        }

        if (existing.IsTemplate)
        {
            throw new InvalidOperationException("Templates do not have host availability. Create a runnable runbook from the template first.");
        }

        var runbookGroup = ResolveLogicalRunbookGroup(allCommands, existing);
        var primaryHostId = normalizedHostIds.Contains(existing.HostId)
            ? existing.HostId
            : normalizedHostIds[0];

        await SaveRunnableRunbookGroupAsync(
            runbookGroup,
            existing.Id,
            primaryHostId,
            normalizedHostIds,
            existing.Name,
            NormalizeRunbookContent(existing.CommandText),
            existing.Description,
            existing.RequiresSudo,
            existing.IsQuickAccess,
            existing.IsGlobalFavorite,
            existing.TemplateSourceId,
            existing.ParameterDefinitions ?? [],
            existing.ParameterValueSnapshot ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
    }

    public async Task SetHostRunbookAssignmentsAsync(Guid hostId, IReadOnlyList<Guid> runbookIds, CancellationToken cancellationToken = default)
    {
        var host = await hostStore.GetAsync(hostId, cancellationToken);
        if (host is null)
        {
            throw new InvalidOperationException("Machine not found.");
        }

        var selectedRunbookIds = runbookIds
            .Where(runbookId => runbookId != Guid.Empty)
            .Distinct()
            .ToHashSet();
        var runbooks = await ListCommandsAsync(cancellationToken);
        foreach (var runbook in runbooks.Where(command => !command.IsTemplate))
        {
            var currentlyAssigned = runbook.HostIds.Contains(hostId);
            var shouldBeAssigned = selectedRunbookIds.Contains(runbook.Id);
            if (currentlyAssigned == shouldBeAssigned)
            {
                continue;
            }

            var updatedHostIds = shouldBeAssigned
                ? runbook.HostIds.Concat([hostId]).Distinct().ToArray()
                : runbook.HostIds.Where(existingHostId => existingHostId != hostId).ToArray();
            if (updatedHostIds.Length == 0)
            {
                continue;
            }

            await SetRunbookHostsAsync(runbook.Id, updatedHostIds, cancellationToken);
        }
    }

    public async Task SetCommandQuickAccessAsync(Guid commandId, bool isQuickAccess, CancellationToken cancellationToken = default)
    {
        var allCommands = await savedCommandStore.ListAsync(cancellationToken);
        var command = allCommands.FirstOrDefault(saved => saved.Id == commandId);
        if (command is null)
        {
            throw new InvalidOperationException("Runbook not found.");
        }

        if (command.IsTemplate)
        {
            throw new InvalidOperationException("Templates cannot be marked as terminal favourites.");
        }

        foreach (var runbook in ResolveLogicalRunbookGroup(allCommands, command))
        {
            await savedCommandStore.SaveAsync(runbook with { IsQuickAccess = isQuickAccess }, cancellationToken);
        }
    }

    public async Task SetCommandGlobalFavoriteAsync(Guid commandId, bool isGlobalFavorite, CancellationToken cancellationToken = default)
    {
        var allCommands = await savedCommandStore.ListAsync(cancellationToken);
        var command = allCommands.FirstOrDefault(saved => saved.Id == commandId);
        if (command is null)
        {
            throw new InvalidOperationException("Runbook not found.");
        }

        if (command.IsTemplate)
        {
            throw new InvalidOperationException("Templates cannot be marked as global terminal favourites.");
        }

        foreach (var runbook in ResolveLogicalRunbookGroup(allCommands, command))
        {
            await savedCommandStore.SaveAsync(runbook with { IsGlobalFavorite = isGlobalFavorite }, cancellationToken);
        }
    }

    public async Task<StarterRunbookImportResult> ImportStarterRunbooksAsync(CancellationToken cancellationToken = default)
    {
        var hosts = await hostStore.ListAsync(cancellationToken);
        var targetHost = hosts
            .OrderBy(host => AiLocalMachine.IsLocalMachine(host.Id) ? 0 : 1)
            .ThenBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (targetHost is null)
        {
            throw new InvalidOperationException("Add a machine before importing starter runbooks.");
        }

        var existingCommands = await savedCommandStore.ListByHostAsync(targetHost.Id, cancellationToken);
        var importedCount = 0;
        var existingCount = 0;

        foreach (var starter in StarterRunbooks)
        {
            var normalizedStarterCommandText = NormalizeRunbookContent(starter.CommandText);
            var alreadyExists = existingCommands.Any(command =>
                string.Equals(command.Name, starter.Name, StringComparison.OrdinalIgnoreCase) &&
                NormalizeRunbookContent(command.CommandText).Equals(normalizedStarterCommandText, StringComparison.Ordinal));
            if (alreadyExists)
            {
                existingCount++;
                continue;
            }

            await savedCommandStore.SaveAsync(
                new SavedCommand(
                    Guid.NewGuid(),
                    targetHost.Id,
                    starter.Name,
                    normalizedStarterCommandText,
                    starter.Description,
                    starter.RequiresSudo),
                cancellationToken);
            importedCount++;
        }

        return new StarterRunbookImportResult(targetHost.Name, importedCount, existingCount);
    }

    private async Task<RunbookExecutionHostResultViewModel> ExecuteRunbookAsync(
        Guid runbookId,
        SavedCommand command,
        IReadOnlyDictionary<Guid, ManagedHost> hostsById,
        IProgress<RunbookExecutionProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var host = ResolveRunbookHost(command.HostId, hostsById);
        if (host is null)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            progress?.Report(new RunbookExecutionProgressUpdate(
                runbookId,
                command.HostId,
                "Unknown host",
                RunbookExecutionProgressKind.Failed,
                "The target host for this runbook could not be found.",
                string.Empty,
                -1,
                false,
                completedAtUtc));
            return new RunbookExecutionHostResultViewModel(
                command.HostId,
                "Unknown host",
                false,
                -1,
                "The target host for this runbook could not be found.",
                string.Empty,
                string.Empty,
                completedAtUtc,
                completedAtUtc);
        }

        var executionCommand = BuildRunbookExecutionCommand(command, host);

        try
        {
            var executionProgress = progress is null
                ? null
                : new RunbookCommandExecutionProgressAdapter(runbookId, host, progress);
            var result = await commandExecutionService.ExecuteAsync(
                host,
                executionCommand,
                progress: executionProgress,
                cancellationToken);

            return MapRunbookExecutionResult(host, result.IsSuccess, result.ExitCode, result.StandardOutput, result.StandardError, result.StartedAtUtc, result.CompletedAtUtc);
        }
        catch (Exception exception)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            progress?.Report(new RunbookExecutionProgressUpdate(
                runbookId,
                host.Id,
                host.Name,
                RunbookExecutionProgressKind.Failed,
                exception.Message,
                string.Empty,
                -1,
                false,
                completedAtUtc));
            return new RunbookExecutionHostResultViewModel(
                host.Id,
                host.Name,
                false,
                -1,
                exception.Message,
                string.Empty,
                exception.Message,
                completedAtUtc,
                completedAtUtc);
        }
    }

    private static ManagedHost? ResolveRunbookHost(
        Guid hostId,
        IReadOnlyDictionary<Guid, ManagedHost> hostsById)
    {
        if (AiLocalMachine.IsLocalMachine(hostId))
        {
            return AiLocalMachine.CreateManagedHost();
        }

        return hostsById.TryGetValue(hostId, out var host)
            ? host
            : null;
    }

    private static string BuildRunbookExecutionCommand(SavedCommand command, ManagedHost host)
    {
        var baseCommand = RunbookExecutionCommandBuilder.BuildBatchExecutionCommand(command.CommandText, command.RequiresSudo);
        if (string.IsNullOrWhiteSpace(baseCommand))
        {
            return string.Empty;
        }

        var workingDirectory = ManagedHostPathSupport.NormalizeWorkingDirectory(host.DefaultWorkingDirectory, host.Username);
        return string.IsNullOrWhiteSpace(workingDirectory)
            ? baseCommand
            : $"cd {ManagedHostPathSupport.QuoteShellArgument(workingDirectory)} && {baseCommand}";
    }

    private static RunbookExecutionHostResultViewModel MapRunbookExecutionResult(
        ManagedHost host,
        bool success,
        int exitCode,
        string standardOutput,
        string standardError,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        var summary = success
            ? "Runbook completed successfully."
            : ManagedHostPathSupport.FirstNonEmptyLine(standardError, standardOutput) ?? $"Runbook failed with exit code {exitCode}.";

        return new RunbookExecutionHostResultViewModel(
            host.Id,
            host.Name,
            success,
            exitCode,
            summary,
            standardOutput,
            standardError,
            startedAtUtc,
            completedAtUtc);
    }

    private static void ReportRunbookExecutionProgress(
        Guid runbookId,
        ManagedHost host,
        CommandExecutionUpdate update,
        IProgress<RunbookExecutionProgressUpdate> progress)
    {
        switch (update)
        {
            case CommandExecutionStartedUpdate started:
                progress.Report(new RunbookExecutionProgressUpdate(
                    runbookId,
                    host.Id,
                    host.Name,
                    RunbookExecutionProgressKind.Started,
                    AiLocalMachine.IsLocalMachine(host.Id) ? "Runbook started on the LMS host." : "Runbook started over SSH.",
                    string.Empty,
                    null,
                    false,
                    started.StartedAtUtc));
                break;
            case CommandExecutionOutputUpdate output:
                progress.Report(new RunbookExecutionProgressUpdate(
                    runbookId,
                    host.Id,
                    host.Name,
                    output.Channel == CommandExecutionOutputChannel.StandardError
                        ? RunbookExecutionProgressKind.StandardError
                        : RunbookExecutionProgressKind.StandardOutput,
                    output.Channel == CommandExecutionOutputChannel.StandardError ? "stderr" : "stdout",
                    output.Content,
                    null,
                    output.IsCompleteSnapshot,
                    output.OccurredAtUtc));
                break;
            case CommandExecutionCompletedUpdate completed:
                progress.Report(new RunbookExecutionProgressUpdate(
                    runbookId,
                    host.Id,
                    host.Name,
                    completed.ExitCode == 0 ? RunbookExecutionProgressKind.Completed : RunbookExecutionProgressKind.Failed,
                    completed.ExitCode == 0 ? "Runbook completed successfully." : $"Runbook failed with exit code {completed.ExitCode}.",
                    string.Empty,
                    completed.ExitCode,
                    false,
                    completed.CompletedAtUtc));
                break;
        }
    }

    private sealed class RunbookCommandExecutionProgressAdapter(
        Guid runbookId,
        ManagedHost host,
        IProgress<RunbookExecutionProgressUpdate> progress) : IProgress<CommandExecutionUpdate>
    {
        public void Report(CommandExecutionUpdate value)
        {
            ReportRunbookExecutionProgress(runbookId, host, value, progress);
        }
    }

    private static string NormalizeRunbookContent(string content)
    {
        return RunbookExecutionCommandBuilder.NormalizeStoredScript(content);
    }

    private async Task SaveRunnableRunbookGroupAsync(
        IReadOnlyList<SavedCommand> existingCommands,
        Guid primaryRunbookId,
        Guid primaryHostId,
        IReadOnlyList<Guid> selectedHostIds,
        string name,
        string commandText,
        string description,
        bool requiresSudo,
        bool isQuickAccess,
        bool isGlobalFavorite,
        Guid? templateSourceId,
        IReadOnlyList<RunbookParameterDefinition> parameterDefinitions,
        IReadOnlyDictionary<string, string> parameterValueSnapshot,
        CancellationToken cancellationToken)
    {
        var normalizedHostIds = selectedHostIds
            .Distinct()
            .ToArray();
        if (normalizedHostIds.Length == 0)
        {
            throw new InvalidOperationException("Pick at least one machine for this runbook.");
        }

        var groupId = normalizedHostIds.Length > 1
            ? existingCommands.FirstOrDefault(command => command.LinkGroupId.HasValue)?.LinkGroupId ?? Guid.NewGuid()
            : (Guid?)null;
        var existingByHostId = existingCommands.ToDictionary(command => command.HostId);
        var retainedCommandIds = new HashSet<Guid>();

        foreach (var hostId in normalizedHostIds)
        {
            var savedId = hostId == primaryHostId
                ? primaryRunbookId
                : existingByHostId.TryGetValue(hostId, out var existingForHost) && existingForHost.Id != primaryRunbookId
                    ? existingForHost.Id
                    : Guid.NewGuid();
            retainedCommandIds.Add(savedId);

            await savedCommandStore.SaveAsync(
                new SavedCommand(
                    savedId,
                    hostId,
                    name,
                    commandText,
                    description,
                    requiresSudo,
                    isQuickAccess,
                    false,
                    templateSourceId,
                    groupId,
                    parameterDefinitions,
                    parameterValueSnapshot,
                    isGlobalFavorite),
                cancellationToken);
        }

        foreach (var removed in existingCommands.Where(command => !retainedCommandIds.Contains(command.Id)))
        {
            await savedCommandStore.DeleteAsync(removed.Id, cancellationToken);
        }
    }

    private static IReadOnlyList<SavedCommand> ResolveLogicalRunbookGroup(
        IReadOnlyList<SavedCommand> allCommands,
        SavedCommand target)
    {
        var groupKey = BuildRunbookLibraryGroupKey(target);
        return allCommands
            .Where(command => string.Equals(BuildRunbookLibraryGroupKey(command), groupKey, StringComparison.Ordinal))
            .ToArray();
    }

    private static string BuildRunbookLibraryGroupKey(SavedCommand command)
    {
        if (command.LinkGroupId.HasValue)
        {
            return $"link:{command.LinkGroupId.Value:D}";
        }

        if (command.IsTemplate)
        {
            return $"template:{command.Id:D}";
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"copy:{command.Name}\u001f{command.Description}\u001f{NormalizeRunbookContent(command.CommandText)}\u001f{command.RequiresSudo}\u001f{command.IsQuickAccess}\u001f{command.IsGlobalFavorite}\u001f{command.TemplateSourceId?.ToString("D") ?? string.Empty}\u001f{SerializeRunbookParameterDefinitions(command.ParameterDefinitions)}\u001f{SerializeRunbookParameterValues(command.ParameterValueSnapshot)}");
    }

    private static string SerializeRunbookParameterDefinitions(IReadOnlyList<RunbookParameterDefinition>? definitions)
    {
        if (definitions is null || definitions.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\u001e",
            definitions
                .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .Select(definition =>
                    $"{definition.Name}\u001d{definition.Label}\u001d{definition.Kind}\u001d{definition.Placeholder}\u001d{definition.HelpText}\u001d{definition.IsRequired}"));
    }

    private static string SerializeRunbookParameterValues(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\u001e",
            values
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{entry.Key}\u001d{entry.Value}"));
    }

    private static IReadOnlyList<Guid> NormalizeSelectedHostIds(
        RunbookEditor editor,
        IReadOnlyDictionary<Guid, ManagedHost> hostsById)
    {
        var selected = editor.SelectedHostIds
            .Where(hostsById.ContainsKey)
            .Distinct()
            .ToList();

        if (selected.Count == 0)
        {
            if (!hostsById.ContainsKey(editor.HostId))
            {
                throw new InvalidOperationException("Pick a valid machine for this runbook.");
            }

            selected.Add(editor.HostId);
        }

        if (!selected.Contains(editor.HostId))
        {
            editor.HostId = selected[0];
        }

        return selected;
    }

    private static string BuildHostSummary(IReadOnlyList<string> hostNames) =>
        hostNames.Count switch
        {
            0 => "No machine",
            1 => hostNames[0],
            2 => $"{hostNames[0]} + {hostNames[1]}",
            _ => $"{hostNames[0]} + {hostNames.Count - 1} more"
        };

    private sealed record StarterRunbookTemplate(
        string Name,
        string CommandText,
        string Description,
        bool RequiresSudo);
}
