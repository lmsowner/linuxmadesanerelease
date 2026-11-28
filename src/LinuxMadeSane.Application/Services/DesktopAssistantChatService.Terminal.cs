// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Collections.Concurrent;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Contracts.DesktopAssistant;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.DesktopSession;
namespace LinuxMadeSane.Application.Services;

public sealed partial class DesktopAssistantChatService
{
    private const string TerminalStatePrefix = "LMS-DESKTOP-TERMINAL-STATE:";
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TerminalGates = new();
    // Like terminal approval, this lasts only for this conversation and live helper connection.
    // It is deliberately absent from saved chat history and machine configuration.
    private static readonly ConcurrentDictionary<Guid, string> TerminalSessionApprovals = new();
    private static bool HasSessionApproval(Guid id, DesktopTerminalState state) =>
        TerminalSessionApprovals.TryGetValue(id, out var connection) && connection == state.ConnectionId;
    private sealed record DesktopTerminalState(string ConnectionId, int? UserId, string UserName, string Goal,
        string PendingCommand = "", string Evidence = "", string Directory = "");
    private static DesktopTerminalState? ReadTerminalState(IReadOnlyList<AiChatMessage> messages)
    {
        var saved = messages.OrderByDescending(m => m.SequenceNumber).FirstOrDefault(m => m.Role == AiChatMessageRole.System && m.Content.StartsWith(TerminalStatePrefix, StringComparison.Ordinal));
        return saved is null ? null : JsonSerializer.Deserialize<DesktopTerminalState>(saved.Content[TerminalStatePrefix.Length..]);
    }
    private static DesktopAssistantProposedFixViewModel CommandApproval(string command) => new(DesktopSessionActionKinds.RunTerminalCommand,
        new Dictionary<string, string> { ["command"] = command }, "Approve command", command);
    private async Task AppendTerminalMessageAsync(Guid id, AiChatMessageRole role, string text, CancellationToken token)
    {
        var messages = await conversationStore.ListMessagesAsync(id, token);
        await conversationStore.SaveMessageAsync(new(Guid.NewGuid(), id, GetNextSequenceNumber(messages), role, text, DateTimeOffset.UtcNow), token);
    }
    private Task SaveTerminalStateAsync(Guid id, DesktopTerminalState state, CancellationToken token) =>
        AppendTerminalMessageAsync(id, AiChatMessageRole.System, TerminalStatePrefix + JsonSerializer.Serialize(state), token);
    private async Task<DesktopSessionRegistration?> SelectCommandBridgeAsync(DesktopSessionBrokerSnapshot snapshot, DesktopTerminalState? previous, CancellationToken token)
    {
        var preferred = previous?.UserName;
        if (string.IsNullOrWhiteSpace(preferred) && sessionPreferences is not null)
            preferred = (await sessionPreferences.InspectAsync(token)).PreferredUserName;
        var candidates = snapshot.Sessions.Where(session => session.IsConnected && session.CapabilityReport.UserId is >= 1000 and < 65534 && session.CapabilityReport.SessionClass != "greeter" &&
            (session.CapabilityReport.CanLaunchGuiApps || session.CapabilityReport.SupportsTerminalCommands));
        if (!string.IsNullOrWhiteSpace(preferred))
            return candidates.Where(session => session.CapabilityReport.UserName == preferred &&
                    (previous is null || session.CapabilityReport.UserId == previous.UserId))
                .OrderByDescending(session => session.CapabilityReport.CanLaunchGuiApps).ThenByDescending(session => session.LastSeenAtUtc).FirstOrDefault();
        if (candidates.Where(session => session.CapabilityReport.CanLaunchGuiApps).OrderByDescending(session => session.LastSeenAtUtc).FirstOrDefault() is { } graphical) return graphical;
        var users = candidates.GroupBy(session => (session.CapabilityReport.UserId, session.CapabilityReport.UserName)).ToArray();
        return users.Length == 1 ? users[0].OrderByDescending(session => session.LastSeenAtUtc).First() : null;
    }
    private DesktopSessionRegistration BoundDesktop(DesktopTerminalState state) => desktopSessionBroker.GetSnapshot().Sessions
        .FirstOrDefault(s => s.ConnectionId == state.ConnectionId && s.IsConnected && s.CapabilityReport.UserId == state.UserId &&
            s.CapabilityReport.UserName == state.UserName && (s.CapabilityReport.CanLaunchGuiApps || s.CapabilityReport.SupportsTerminalCommands))
        ?? throw new InvalidOperationException("The desktop used for this task disconnected. Open the desktop and send the request again.");
    private async Task<DesktopAssistantChatWorkspaceViewModel> SendSharedTerminalMessageAsync(Guid? sessionId, string message, string? providerKey, string? modelId, CancellationToken token)
    {
        var text = NormalizeMessage(message);
        var id = sessionId ?? await CreateSessionAsync(providerKey, modelId, token);
        var gate = TerminalGates.GetOrAdd(id, _ => new(1, 1)); await gate.WaitAsync(token);
        try
        {
            var thread = await conversationStore.GetThreadAsync(id, token) ?? throw new InvalidOperationException("Chat session unavailable.");
            if (!IsDesktopAssistantThread(thread)) throw new InvalidOperationException("This is not a Desktop Assistant chat.");
            var previous = ReadTerminalState(await conversationStore.ListMessagesAsync(id, token));
            var refreshed = await RefreshDesktopEvidenceAsync(desktopSessionBroker.GetSnapshot(), token);
            var desktop = await SelectCommandBridgeAsync(refreshed, previous, token) ??
                throw new InvalidOperationException("The configured user's machine connection is not ready. Repair Desktop Helper, or use Repair in Desktop Setup to recover without AI.");
            var state = new DesktopTerminalState(desktop.ConnectionId, desktop.CapabilityReport.UserId, desktop.CapabilityReport.UserName, text);
            await AppendTerminalMessageAsync(id, AiChatMessageRole.User, text, token);
            await SaveTerminalStateAsync(id, state, token);
            var provider = await ResolveProviderAsync(providerKey ?? thread.ProviderKey, true, token) ?? throw new InvalidOperationException("No AI provider configured.");
            thread = thread with
            {
                ProviderKey = provider.ProviderKey,
                ProviderType = provider.Definition.ProviderType,
                ModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await conversationStore.SaveThreadAsync(thread, token);
            return await RunSharedTerminalAsync(thread, state, text, token);
        }
        finally { gate.Release(); }
    }
    public async Task<DesktopAssistantChatWorkspaceViewModel> ExecuteTerminalCommandAsync(Guid sessionId,
        string command, DesktopSessionBrokerSnapshot desktopSnapshot, CancellationToken cancellationToken = default,
        bool allowForSession = false)
    {
        var gate = TerminalGates.GetOrAdd(sessionId, _ => new(1, 1)); await gate.WaitAsync(cancellationToken);
        try
        {
            var thread = await conversationStore.GetThreadAsync(sessionId, cancellationToken) ?? throw new InvalidOperationException("Chat session unavailable.");
            if (!IsDesktopAssistantThread(thread)) throw new InvalidOperationException("This is not a Desktop Assistant chat.");
            var state = ReadTerminalState(await conversationStore.ListMessagesAsync(sessionId, cancellationToken));
            if (state is null || string.IsNullOrEmpty(state.PendingCommand) || !string.Equals(state.PendingCommand, command, StringComparison.Ordinal))
                throw new InvalidOperationException("This command approval is no longer current. Send the request again.");
            _ = BoundDesktop(state);
            if (allowForSession) TerminalSessionApprovals[sessionId] = state.ConnectionId;
            // Consume the stored approval before executing. Browser-supplied replacement commands cannot run.
            state = state with { PendingCommand = "" }; await SaveTerminalStateAsync(sessionId, state, cancellationToken);
            state = await ExecuteDesktopTerminalAsync(sessionId, state, command, cancellationToken);
            return await RunSharedTerminalAsync(thread, state, "", cancellationToken);
        }
        finally { gate.Release(); }
    }
    private async Task<DesktopAssistantChatWorkspaceViewModel> RunSharedTerminalAsync(AiChatThread thread, DesktopTerminalState state, string request, CancellationToken token)
    {
        var outcome = await TerminalAiAgentFlow.RunAsync(async (_, _, token) =>
        {
            var desktop = BoundDesktop(state); var report = desktop.CapabilityReport;
            var messages = await conversationStore.ListMessagesAsync(thread.Id, token);
            var conversation = new TerminalAiConversationState
            {
                ProviderKey = thread.ProviderKey,
                ModelId = thread.ModelId,
                ProviderConversationReference = thread.ProviderConversationReference,
                ProviderResponseId = thread.ProviderStateReference,
                Entries = messages.Where(m => !m.Content.StartsWith(TerminalStatePrefix, StringComparison.Ordinal)).TakeLast(16)
                    .Select(m => new TerminalAiConversationEntry(m.Role, m.Content, m.CreatedAtUtc)).ToList()
            };
            var result = await sharedTerminalAssistant.ExecutePromptAsync(conversation, new()
            {
                HostName = report.MachineName,
                Username = report.UserName,
                WorkingDirectory = state.Directory,
                Request = request,
                ActiveGoal = state.Goal,
                StructuredCommandOutput = state.Evidence,
                AccessMode = TerminalAiAccessMode.Agent,
                DesktopContext = $"The native Avalonia client is assisting {report.UserName}'s {report.CurrentDesktop ?? report.DesktopSession} desktop. Display={report.Display}; Wayland={report.WaylandDisplay}; runtime={report.XdgRuntimeDirectory}. Commands execute as this desktop user. You have the terminal assistant command-and-result workflow: collect diagnostics now instead of claiming another LMS action must be implemented. Older chat limitations are obsolete. Preserve running applications. Launch GUI apps without blocking the command channel, then verify the actual window or tray icon, not just a process.\nObserved desktop evidence:\n" + string.Join("\n", report.ReadOnlyDiagnostics.Take(24).Select(p => p.Key + ": " + (p.Value.Length > 250 ? p.Value[..250] : p.Value)))
                    + (!report.IsGuiSessionAvailable ? "\nThe graphical desktop is not running. The connected user agent can still inspect logs and repair startup; do not require a working GUI before investigating." : "")
                    + "\nFor GNOME startup failures, check installed session entries, GDM's WaylandEnable setting and kernel graphics errors. Wayland-only GNOME cannot start with Wayland disabled. If the kernel reports 'Cursor dimensions' exceeding 'device max 0', verify and consider MUTTER_DEBUG_DISABLE_HW_CURSORS=1. Back up settings before changing them. Verify the screen after restart; active processes alone do not prove it works."
            }, token);
            request = "";
            await AppendTerminalMessageAsync(thread.Id, AiChatMessageRole.Assistant, result.AssistantEntry.Content, token);
            thread = thread with
            {
                ProviderConversationReference = result.ProviderConversationReference,
                ProviderStateReference = result.ProviderResponseId,
                ModelId = result.ModelId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await conversationStore.SaveThreadAsync(thread, token);
            await auditService.RecordAsync(new(Guid.NewGuid(), thread.Id, null, "desktop-assistant.chat.turn", "Shared terminal AI turn",
                $"Desktop user {state.UserName}; session {state.ConnectionId}; sanitization: {result.Sanitization.Message}", AiExecutionOutcome.Succeeded, DateTimeOffset.UtcNow), token);
            var command = result.AssistantEntry.SuggestedCommand.Trim();
            if (string.IsNullOrEmpty(command)) { await SaveTerminalStateAsync(thread.Id, state with { PendingCommand = "" }, token); return TerminalAiAgentStep.Complete; }
            if (terminalCommandPolicy?.CanRun(command, HasSessionApproval(thread.Id, state)) != true)
            {
                await SaveTerminalStateAsync(thread.Id, state with { PendingCommand = command }, token);
                return TerminalAiAgentStep.Pause;
            }
            state = await ExecuteDesktopTerminalAsync(thread.Id, state, command, token);
            return TerminalAiAgentStep.CommandCompleted;
        }, cancellationToken: token);
        if (!outcome.CommandLimitReached) return await GetWorkspaceAsync(thread.Id, desktopSessionBroker.GetSnapshot(), token);
        await SaveTerminalStateAsync(thread.Id, state with { PendingCommand = "" }, token);
        await AppendTerminalMessageAsync(thread.Id, AiChatMessageRole.System, "Paused after eight commands. Send Continue to carry on with the task.", token);
        return await GetWorkspaceAsync(thread.Id, desktopSessionBroker.GetSnapshot(), token);
    }
    private async Task<DesktopTerminalState> ExecuteDesktopTerminalAsync(Guid id, DesktopTerminalState state, string command, CancellationToken token)
    {
        var desktop = BoundDesktop(state);
        await AppendTerminalMessageAsync(id, AiChatMessageRole.System, "$ " + command + "\nRunning in " + state.UserName + "’s desktop…", token);
        var result = await desktopSessionBroker.ExecuteActionAsync(desktop.ConnectionId, new(Guid.NewGuid(), DesktopSessionActionKinds.RunTerminalCommand,
            new Dictionary<string, string> { ["command"] = command, ["directory"] = state.Directory }, DateTimeOffset.UtcNow), TimeSpan.FromMinutes(3), token);
        var evidence = $"Command: {command}\nExit code: {result.Diagnostics.GetValueOrDefault("exitCode", result.Succeeded ? "0" : "1")}\nStandard output:\n{result.Diagnostics.GetValueOrDefault("stdout", "")}\nStandard error:\n{result.Diagnostics.GetValueOrDefault("stderr", result.Detail)}";
        await AppendTerminalMessageAsync(id, AiChatMessageRole.System, evidence, token);
        state = state with { Evidence = evidence, Directory = result.Diagnostics.GetValueOrDefault("directory", state.Directory), PendingCommand = "" };
        await SaveTerminalStateAsync(id, state, token); return state;
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> StopTerminalTaskAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        TerminalSessionApprovals.TryRemove(sessionId, out _);
        var gate = TerminalGates.GetOrAdd(sessionId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var thread = await conversationStore.GetThreadAsync(sessionId, cancellationToken);
            if (thread is null || !IsDesktopAssistantThread(thread)) throw new InvalidOperationException("This is not a Desktop Assistant chat.");
            var state = ReadTerminalState(await conversationStore.ListMessagesAsync(sessionId, cancellationToken));
            if (state is not null) await SaveTerminalStateAsync(sessionId, state with { PendingCommand = "" }, cancellationToken);
            return await GetWorkspaceAsync(sessionId, desktopSessionBroker.GetSnapshot(), cancellationToken);
        }
        finally { gate.Release(); }
    }
}
