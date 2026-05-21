// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.DesktopAssistant;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Application.Services;

public sealed partial class DesktopAssistantChatService(
    IAiConversationStore conversationStore,
    IAiProviderRegistry providerRegistry,
    IAiAuditService auditService,
    IDesktopSessionBroker desktopSessionBroker,
    IAiPromptSanitizer promptSanitizer) : IDesktopAssistantChatService
{
    private const string ThreadTitlePrefix = "Desktop Assistant";
    private const int MaxHistoryEntries = 14;
    private const int MaxHistoryRecapChars = 2200;
    private const int MaxHistoryEntryChars = 320;
    private const int MaxPromptChars = 4000;
    private const int MaxDesktopProcessRows = 42;
    private const int MaxProcessCommandChars = 150;
    private static readonly TimeSpan DesktopActionTimeout = TimeSpan.FromSeconds(12);

    public async Task<DesktopAssistantChatWorkspaceViewModel> GetWorkspaceAsync(
        Guid? sessionId,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        CancellationToken cancellationToken = default)
    {
        var sessions = await ListSessionsAsync(cancellationToken);
        var activeSessionId = ResolveActiveSessionId(sessionId, sessions);
        var activeThread = activeSessionId.HasValue
            ? await conversationStore.GetThreadAsync(activeSessionId.Value, cancellationToken)
            : null;
        var messages = activeSessionId.HasValue
            ? await conversationStore.ListMessagesAsync(activeSessionId.Value, cancellationToken)
            : [];
        var provider = await ResolveProviderAsync(activeThread?.ProviderKey, false, cancellationToken);
        var providerKey = activeThread?.ProviderKey ?? provider?.ProviderKey ?? string.Empty;
        var modelId = string.IsNullOrWhiteSpace(activeThread?.ModelId)
            ? provider?.Settings.DefaultModelId ?? string.Empty
            : activeThread.ModelId;

        return new DesktopAssistantChatWorkspaceViewModel(
            sessions,
            activeSessionId,
            messages,
            desktopSnapshot.BestAvailableSession is not null,
            provider is not null,
            providerKey,
            provider?.Settings.DisplayName ?? "No AI provider configured",
            modelId,
            BuildStatusSummary(desktopSnapshot));
    }

    public async Task<Guid> CreateSessionAsync(
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveProviderAsync(providerKey, true, cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, provider.Settings.DefaultModelId);

        var now = DateTimeOffset.UtcNow;
        var thread = new AiChatThread(
            Guid.NewGuid(),
            $"{ThreadTitlePrefix} - {now.ToLocalTime():g}",
            provider.ProviderKey,
            provider.Definition.ProviderType,
            selectedModelId,
            new AiTrustProfile(
                AiTrustLevel.Guided,
                AllowReadOnlyTools: true,
                AllowMutatingTools: false,
                RequireApprovalForMediumRisk: true,
                RequireApprovalForHighRisk: true),
            string.Empty,
            string.Empty,
            now,
            now);

        await conversationStore.SaveThreadAsync(thread, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                null,
                "desktop-assistant.chat.created",
                "Desktop Assistant chat session created",
                $"Provider: {provider.Settings.DisplayName}; model: {thread.ModelId}",
                AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);

        return thread.Id;
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> SendMessageAsync(
        Guid? sessionId,
        string message,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        if (desktopSnapshot.BestAvailableSession is null)
        {
            throw new InvalidOperationException("Desktop Assistant is not ready. Connect the desktop helper before chatting to the desktop.");
        }

        var threadId = sessionId ?? await CreateSessionAsync(providerKey, modelId, cancellationToken);
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That Desktop Assistant chat session could not be found.");
        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        var provider = await ResolveProviderAsync(
                string.IsNullOrWhiteSpace(providerKey) ? thread.ProviderKey : providerKey,
                true,
                cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId);
        var providerChanged =
            !provider.ProviderKey.Equals(thread.ProviderKey, StringComparison.OrdinalIgnoreCase) ||
            !selectedModelId.Equals(thread.ModelId, StringComparison.OrdinalIgnoreCase);

        var userText = NormalizeMessage(message);
        var existingMessages = await conversationStore.ListMessagesAsync(thread.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var nextSequenceNumber = GetNextSequenceNumber(existingMessages);
        var userMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            nextSequenceNumber,
            AiChatMessageRole.User,
            userText,
            now);

        await conversationStore.SaveMessageAsync(userMessage, cancellationToken);
        existingMessages = [.. existingMessages, userMessage];

        var prompt = BuildPrompt(userText, desktopSnapshot, BuildConversationRecap(existingMessages));
        var sanitization = promptSanitizer.Sanitize(prompt, provider.Settings.ProviderType);
        var providerThread = thread with
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.Definition.ProviderType,
            ModelId = selectedModelId,
            ProviderConversationReference = providerChanged ? string.Empty : thread.ProviderConversationReference,
            ProviderStateReference = providerChanged ? string.Empty : thread.ProviderStateReference,
            TrustProfile = thread.TrustProfile with { AllowMutatingTools = false }
        };

        var result = await provider.ExecuteTurnAsync(
            new AiProviderTurnRequest(
                providerThread,
                existingMessages,
                [],
                [new AiProviderMessageInputItem(AiChatMessageRole.User, sanitization.Content)],
                [],
                provider.Settings.StreamingEnabled,
                InternetResearchAllowed: false),
            cancellationToken: cancellationToken);

        var assistantText = string.Join(
                Environment.NewLine + Environment.NewLine,
                result.AssistantOutputs
                    .Select(output => output.Content?.Trim())
                    .Where(static content => !string.IsNullOrWhiteSpace(content)))
            .Trim();
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new InvalidOperationException("The AI provider returned no assistant text.");
        }

        var assistantMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            nextSequenceNumber + 1,
            AiChatMessageRole.Assistant,
            assistantText,
            DateTimeOffset.UtcNow);
        await conversationStore.SaveMessageAsync(assistantMessage, cancellationToken);

        await conversationStore.SaveThreadAsync(providerThread with
        {
            ProviderConversationReference = result.ConversationReference ?? providerThread.ProviderConversationReference,
            ProviderStateReference = result.ProviderResponseId,
            UpdatedAtUtc = assistantMessage.CreatedAtUtc
        }, cancellationToken);

        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                userMessage.Id,
                "desktop-assistant.chat.turn",
                "Desktop Assistant chat turn completed",
                $"Desktop context: {BuildStatusSummary(desktopSnapshot)}; sanitization: {sanitization.Summary}",
                AiExecutionOutcome.Succeeded,
                assistantMessage.CreatedAtUtc),
            cancellationToken);

        return await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> ApplyKeyboardLayoutAsync(
        Guid? sessionId,
        string layout,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var session = desktopSnapshot.BestAvailableSession
            ?? throw new InvalidOperationException("Desktop Assistant is not ready. Connect the desktop helper before applying desktop fixes.");
        var normalizedLayout = NormalizeKeyboardLayout(layout);
        var threadId = sessionId ?? await CreateSessionAsync(providerKey, modelId, cancellationToken);
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That Desktop Assistant chat session could not be found.");
        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        var provider = await ResolveProviderAsync(
                string.IsNullOrWhiteSpace(providerKey) ? thread.ProviderKey : providerKey,
                true,
                cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId);
        var existingMessages = await conversationStore.ListMessagesAsync(thread.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var userMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            GetNextSequenceNumber(existingMessages),
            AiChatMessageRole.User,
            $"Deep Fix: set keyboard layout to {normalizedLayout}.",
            now);
        await conversationStore.SaveMessageAsync(userMessage, cancellationToken);

        var request = new DesktopSessionActionRequest(
            Guid.NewGuid(),
            DesktopSessionActionKinds.SetKeyboardLayout,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["layout"] = normalizedLayout
            },
            DateTimeOffset.UtcNow);
        var actionResult = await desktopSessionBroker.ExecuteActionAsync(
            session.ConnectionId,
            request,
            DesktopActionTimeout,
            cancellationToken);

        var assistantMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            userMessage.SequenceNumber + 1,
            AiChatMessageRole.Assistant,
            BuildKeyboardLayoutActionMessage(normalizedLayout, actionResult),
            DateTimeOffset.UtcNow);
        await conversationStore.SaveMessageAsync(assistantMessage, cancellationToken);

        await conversationStore.SaveThreadAsync(thread with
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.Definition.ProviderType,
            ModelId = selectedModelId,
            UpdatedAtUtc = assistantMessage.CreatedAtUtc
        }, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                userMessage.Id,
                "desktop-assistant.keyboard-layout.applied",
                actionResult.Summary,
                actionResult.Detail,
                actionResult.Succeeded ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
                assistantMessage.CreatedAtUtc),
            cancellationToken);

        return await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
    }

    private async Task<IReadOnlyList<DesktopAssistantChatSessionViewModel>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var threads = await conversationStore.ListThreadsAsync(cancellationToken);
        var messages = await conversationStore.ListMessagesAsync(cancellationToken: cancellationToken);
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var providerLabels = configuredProviders.ToDictionary(
            provider => provider.ProviderKey,
            provider => provider.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        var messageCounts = messages
            .GroupBy(message => message.ThreadId)
            .ToDictionary(group => group.Key, group => group.Count(message => message.Role != AiChatMessageRole.System));

        return threads
            .Where(IsDesktopAssistantThread)
            .OrderByDescending(thread => thread.UpdatedAtUtc)
            .Select(thread => new DesktopAssistantChatSessionViewModel(
                thread.Id,
                thread.Title,
                thread.ProviderKey,
                providerLabels.GetValueOrDefault(thread.ProviderKey, thread.ProviderKey),
                thread.ModelId,
                messageCounts.GetValueOrDefault(thread.Id),
                thread.UpdatedAtUtc))
            .ToArray();
    }

    private async Task<IAiProvider?> ResolveProviderAsync(
        string? providerKey,
        bool requirePreferredProvider,
        CancellationToken cancellationToken)
    {
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var runnableProviders = configuredProviders
            .Where(provider =>
                provider.IsEnabled &&
                providerRegistry.FindDefinition(provider.ProviderType)?.IsRuntimeImplemented != false)
            .ToArray();

        var normalizedProviderKey = NormalizeSelection(providerKey);
        var selectedProvider = !string.IsNullOrWhiteSpace(normalizedProviderKey)
            ? runnableProviders.FirstOrDefault(provider => provider.ProviderKey.Equals(normalizedProviderKey, StringComparison.OrdinalIgnoreCase))
            : null;
        if (selectedProvider is null && requirePreferredProvider && !string.IsNullOrWhiteSpace(normalizedProviderKey))
        {
            throw new InvalidOperationException("The selected AI provider is not enabled or runnable.");
        }

        selectedProvider ??= runnableProviders.FirstOrDefault(provider => provider.IsDefault)
            ?? runnableProviders.FirstOrDefault();

        return selectedProvider is null
            ? null
            : await providerRegistry.GetProviderAsync(selectedProvider.ProviderKey, cancellationToken);
    }

    private static Guid? ResolveActiveSessionId(
        Guid? requestedSessionId,
        IReadOnlyList<DesktopAssistantChatSessionViewModel> sessions)
    {
        if (requestedSessionId.HasValue && sessions.Any(session => session.Id == requestedSessionId.Value))
        {
            return requestedSessionId.Value;
        }

        return sessions.FirstOrDefault()?.Id;
    }

    private static bool IsDesktopAssistantThread(AiChatThread thread) =>
        thread.Title.StartsWith(ThreadTitlePrefix, StringComparison.OrdinalIgnoreCase);

    private static string ResolveModelId(string? requestedModelId, string fallbackModelId)
    {
        var normalizedModelId = NormalizeSelection(requestedModelId);
        return string.IsNullOrWhiteSpace(normalizedModelId)
            ? fallbackModelId
            : normalizedModelId;
    }

    private static string NormalizeSelection(string? value) =>
        value?.Trim() ?? string.Empty;

    private static string NormalizeKeyboardLayout(string layout)
    {
        var normalized = NormalizeSelection(layout).ToLowerInvariant();
        if (!KeyboardLayoutPattern().IsMatch(normalized))
        {
            throw new InvalidOperationException("Keyboard layout code is invalid.");
        }

        return normalized;
    }

    private static string BuildKeyboardLayoutActionMessage(
        string layout,
        DesktopSessionActionResult actionResult)
    {
        var builder = new StringBuilder();
        builder.AppendLine(actionResult.Succeeded
            ? $"Done. Keyboard layout is now set to {layout}."
            : $"I tried to set keyboard layout to {layout}, but the desktop helper reported a problem.");

        if (!string.IsNullOrWhiteSpace(actionResult.Summary))
        {
            builder.AppendLine(actionResult.Summary.Trim());
        }

        var keyboardDiagnostics = actionResult.Diagnostics
            .Where(item => item.Key.StartsWith("keyboard.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (keyboardDiagnostics.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Verified keyboard evidence:");
            foreach (var diagnostic in keyboardDiagnostics)
            {
                builder.AppendLine($"- {diagnostic.Key}: {Summarize(diagnostic.Value)}");
            }
        }

        return builder.ToString().Trim();
    }

    private static int GetNextSequenceNumber(IReadOnlyList<AiChatMessage> messages) =>
        messages.Count == 0 ? 1 : messages.Max(message => message.SequenceNumber) + 1;

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Enter a message for Desktop Assistant.");
        }

        var trimmed = message.Trim();
        return trimmed.Length <= MaxPromptChars ? trimmed : $"{trimmed[..MaxPromptChars]}...";
    }

    private static string BuildPrompt(
        string userText,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string conversationRecap)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are Linux Made Sane Desktop Assistant, a GUI-session agent for the signed-in Linux desktop.");
        builder.AppendLine("The user expects an LMS product experience, not a command-line tutorial. Use the current graphical-session facts and LMS-collected evidence below.");
        builder.AppendLine("Be direct and concise. Answer the user's question first, then give the next LMS action.");
        builder.AppendLine("If the evidence already answers the question, answer from the evidence instead of asking for more checks.");
        builder.AppendLine("If the user asks what processes are running, summarize the readable processes owned by the desktop user id and say when this is process ownership evidence rather than an active-window list.");
        builder.AppendLine("If the user asks for Deep Fix, behave like Terminal Deep Fix: keep moving toward the fix, ask for approval only when a desktop-changing action is needed, and avoid long diagnosis essays.");
        builder.AppendLine("Do not ask the user to paste command output back into chat. If fresh evidence is needed, name the single LMS check or future helper action needed.");
        builder.AppendLine("Do not claim you changed the desktop unless the prompt includes an explicit LMS desktop action result.");
        builder.AppendLine("Do not suggest arbitrary sudo or shell-heavy workflows as the main answer. Prefer LMS actions, Desktop Options, setup flows, and helper-backed diagnostics.");
        builder.AppendLine("Known approved action today: set keyboard layout through the Desktop Assistant helper. Other desktop-changing actions should be described as future LMS approvals, not manual shell chores.");
        builder.AppendLine();
        builder.AppendLine("Current desktop session:");
        builder.AppendLine(BuildDesktopContext(desktopSnapshot));

        if (!string.IsNullOrWhiteSpace(conversationRecap))
        {
            builder.AppendLine();
            builder.AppendLine("Recent Desktop Assistant conversation:");
            builder.AppendLine(conversationRecap);
        }

        builder.AppendLine();
        builder.AppendLine("User message:");
        builder.AppendLine(userText);
        return builder.ToString().Trim();
    }

    private static string BuildDesktopContext(DesktopSessionBrokerSnapshot desktopSnapshot)
    {
        var session = desktopSnapshot.BestAvailableSession;
        if (session is null)
        {
            return "No ready desktop helper session is connected.";
        }

        var report = session.CapabilityReport;
        var lines = new List<string>
        {
            $"- User: {report.UserName} ({report.UserId?.ToString() ?? "unknown uid"})",
            $"- Machine: {report.MachineName}",
            $"- Display server: {report.DisplayServer}",
            $"- DISPLAY: {report.Display ?? "not set"}",
            $"- WAYLAND_DISPLAY: {report.WaylandDisplay ?? "not set"}",
            $"- Desktop session: {report.DesktopSession ?? report.CurrentDesktop ?? "unknown"}",
            $"- Session type: {report.SessionType ?? "unknown"}",
            $"- Session bus available: {(report.HasSessionBus ? "yes" : "no")}",
            $"- Can launch GUI apps: {(report.CanLaunchGuiApps ? "yes" : "no")}",
            $"- Available diagnostic tools: {FormatList(report.AvailableTools)}",
            "- LMS desktop surfaces: Assistant chat for GUI issues; Desktop Options for mode/XRDP changes with snapshots; Setup only for helper repair.",
            $"- Approved desktop actions available now: {DesktopSessionActionKinds.SetKeyboardLayout}."
        };

        if (report.Warnings.Count > 0)
        {
            lines.Add($"- Warnings: {string.Join("; ", report.Warnings)}");
        }

        lines.Add("");
        lines.Add("Desktop process evidence:");
        lines.Add(BuildDesktopProcessContext(report));

        if (report.ReadOnlyDiagnostics.Count > 0)
        {
            lines.Add("");
            lines.Add("Read-only helper diagnostics:");
            foreach (var diagnostic in report.ReadOnlyDiagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                lines.Add($"[{diagnostic.Key}]");
                lines.Add(SummarizeDiagnosticOutput(diagnostic.Value));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDesktopProcessContext(DesktopSessionCapabilityReport report)
    {
        if (report.UserId is null)
        {
            return "Desktop user id is unknown, so LMS could not match /proc entries to the desktop user.";
        }

        var processes = EnumerateUserProcesses(report.UserId.Value)
            .OrderBy(process => process.CommandName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.Pid)
            .Take(MaxDesktopProcessRows)
            .ToArray();
        if (processes.Length == 0)
        {
            return $"No readable /proc entries were found for uid {report.UserId.Value}.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Readable processes owned by desktop uid {report.UserId.Value} (first {processes.Length}):");
        foreach (var process in processes)
        {
            builder.AppendLine($"- {process.Pid}: {process.CommandName} - {process.CommandLine}");
        }

        return builder.ToString().Trim();
    }

    private static IEnumerable<DesktopProcessInfo> EnumerateUserProcesses(int userId)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var directoryName = Path.GetFileName(directory);
            if (!int.TryParse(directoryName, out var pid))
            {
                continue;
            }

            if (!TryReadProcessUid(directory, out var processUid) || processUid != userId)
            {
                continue;
            }

            var commandName = ReadProcessName(directory);
            var commandLine = ReadProcessCommandLine(directory);
            if (string.IsNullOrWhiteSpace(commandName) && string.IsNullOrWhiteSpace(commandLine))
            {
                continue;
            }

            yield return new DesktopProcessInfo(
                pid,
                string.IsNullOrWhiteSpace(commandName) ? "unknown" : commandName,
                SummarizeProcessCommand(commandLine, commandName));
        }
    }

    private static bool TryReadProcessUid(string procDirectory, out int userId)
    {
        userId = 0;
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(procDirectory, "status")))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 && int.TryParse(parts[1], out userId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static string ReadProcessName(string procDirectory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(procDirectory, "comm")).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string ReadProcessCommandLine(string procDirectory)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(procDirectory, "cmdline"));
            return raw.Replace('\0', ' ').Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string SummarizeProcessCommand(string commandLine, string commandName)
    {
        var value = string.IsNullOrWhiteSpace(commandLine) ? commandName : commandLine;
        var normalized = MultiWhitespacePattern().Replace(value.Trim(), " ");
        return normalized.Length <= MaxProcessCommandChars
            ? normalized
            : $"{normalized[..MaxProcessCommandChars]}...";
    }

    private static string BuildStatusSummary(DesktopSessionBrokerSnapshot desktopSnapshot)
    {
        var session = desktopSnapshot.BestAvailableSession;
        if (session is null)
        {
            var count = desktopSnapshot.Sessions.Count;
            return count == 0
                ? "Desktop agent is still attaching to the graphical session."
                : $"{count} desktop agent report(s), waiting for a GUI-ready session.";
        }

        var report = session.CapabilityReport;
        return $"{report.DisplayServer} desktop for {report.UserName}; last seen {session.LastSeenAtUtc.ToLocalTime():g}.";
    }

    private static string BuildConversationRecap(IReadOnlyList<AiChatMessage> messages)
    {
        var lines = messages
            .Where(message => message.Role is AiChatMessageRole.User or AiChatMessageRole.Assistant)
            .TakeLast(MaxHistoryEntries)
            .Select(message =>
            {
                var speaker = message.Role == AiChatMessageRole.Assistant ? "Assistant" : "User";
                return $"{speaker}: {Summarize(message.Content)}";
            })
            .ToArray();
        var recap = string.Join(Environment.NewLine, lines);
        return recap.Length <= MaxHistoryRecapChars
            ? recap
            : $"...{recap[^MaxHistoryRecapChars..]}";
    }

    private static string Summarize(string content)
    {
        var normalized = MultiWhitespacePattern().Replace(content.Trim(), " ");
        return normalized.Length <= MaxHistoryEntryChars
            ? normalized
            : $"{normalized[..MaxHistoryEntryChars]}...";
    }

    private static string FormatList(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    private static string SummarizeDiagnosticOutput(string content)
    {
        var normalized = content.Trim();
        return normalized.Length <= 900
            ? normalized
            : $"{normalized[..900]}...";
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespacePattern();

    [GeneratedRegex("^[a-z]{2,3}([_+-][a-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyboardLayoutPattern();

    private sealed record DesktopProcessInfo(int Pid, string CommandName, string CommandLine);
}
