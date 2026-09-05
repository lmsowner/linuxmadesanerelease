// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class TerminalWorkspaceRegistry(ITerminalSessionService terminalSessionService)
{
    private readonly ConcurrentDictionary<string, TerminalWorkspaceState> workspaces = new(StringComparer.Ordinal);

    public TerminalWorkspaceState GetOrCreate(string workspaceId) =>
        workspaces.GetOrAdd(workspaceId, _ => new TerminalWorkspaceState(terminalSessionService));

    public int GetOpenSessionCount(Guid hostId) =>
        workspaces.Values.Sum(workspace =>
            workspace.Tabs.Count(tab =>
                tab.HostId == hostId &&
                tab.ConnectionDesired &&
                tab.SessionId.HasValue));

    public bool HasActiveSession(Guid hostId) =>
        workspaces.Values.Any(workspace =>
            workspace.Tabs.Any(tab =>
                tab.HostId == hostId &&
                tab.ConnectionDesired &&
                (tab.IsSessionActive ||
                 (tab.SessionId.HasValue && tab.Snapshot?.Status is null or TerminalSessionStatus.Starting))));

    public TerminalConnectionSnapshot? FindConnectionSnapshot(Guid hostId) =>
        workspaces.Values
            .SelectMany(workspace => workspace.Tabs)
            .Where(tab => tab.HostId == hostId)
            .OrderByDescending(tab => tab.Snapshot?.LastActivityUtc ?? tab.CreatedAtUtc)
            .Select(tab => new TerminalConnectionSnapshot(
                tab.Username,
                tab.SecretHandle,
                tab.PreferStoredCredentials,
                tab.ConnectionProfileKey,
                tab.ConnectionProfileName))
            .FirstOrDefault(snapshot =>
                !string.IsNullOrWhiteSpace(snapshot.Username) ||
                snapshot.SecretHandle.HasValue ||
                snapshot.PreferStoredCredentials);
}

// Guardrail: workspace state can reference transient secrets by handle only. Raw password
// or private-key text must stay out of shared tab snapshots.
public sealed record TerminalConnectionSnapshot(
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    string ConnectionProfileKey,
    string ConnectionProfileName);

public sealed class TerminalWorkspaceState(ITerminalSessionService terminalSessionService) : IAsyncDisposable
{
    private readonly List<TerminalTabState> tabs = [];
    private readonly object syncRoot = new();
    private long version;

    public bool DefaultCopyOnSelect { get; private set; }

    public IReadOnlyList<TerminalTabState> Tabs
    {
        get
        {
            lock (syncRoot)
            {
                return tabs.ToArray();
            }
        }
    }

    public Guid? ActiveTabId { get; private set; }

    public long Version
    {
        get
        {
            lock (syncRoot)
            {
                return version;
            }
        }
    }

    public TerminalTabState? ActiveTab
    {
        get
        {
            lock (syncRoot)
            {
                return tabs.FirstOrDefault(tab => tab.Id == ActiveTabId)
                    ?? tabs.FirstOrDefault();
            }
        }
    }

    public void SyncHosts(IEnumerable<ManagedHost> hosts)
    {
        var hostMap = hosts.ToDictionary(host => host.Id);

        lock (syncRoot)
        {
            foreach (var tab in tabs)
            {
                if (hostMap.TryGetValue(tab.HostId, out var host))
                {
                    tab.ApplyHost(host);
                }
            }

            if (ActiveTabId.HasValue && tabs.All(tab => tab.Id != ActiveTabId.Value))
            {
                ActiveTabId = tabs.FirstOrDefault()?.Id;
                version++;
            }
        }
    }

    public TerminalTabState EnsureHostTab(ManagedHost host, bool activate = true)
    {
        lock (syncRoot)
        {
            var activeTab = tabs.FirstOrDefault(tab => tab.Id == ActiveTabId);
            var existing = activeTab?.HostId == host.Id
                ? activeTab
                : tabs.FirstOrDefault(tab => tab.HostId == host.Id);
            if (existing is null)
            {
                existing = TerminalTabState.Create(host);
                existing.CopyOnSelect = DefaultCopyOnSelect;
                tabs.Add(existing);
            }
            else
            {
                existing.ApplyHost(host);
            }

            if (activate || ActiveTabId is null)
            {
                ActiveTabId = existing.Id;
                version++;
            }

            return existing;
        }
    }

    public TerminalTabState AddTab(ManagedHost host, bool activate = true) =>
        AddTab(host, null, activate);

    public TerminalTabState AddTab(ManagedHost host, string? workingDirectory, bool activate = true)
    {
        lock (syncRoot)
        {
            var tab = TerminalTabState.Create(host);
            tab.SetWorkingDirectoryOverride(workingDirectory);
            tab.CopyOnSelect = DefaultCopyOnSelect;
            tabs.Add(tab);

            if (activate)
            {
                ActiveTabId = tab.Id;
            }

            version++;
            return tab;
        }
    }

    public void SetActive(Guid tabId)
    {
        lock (syncRoot)
        {
            if (tabs.Any(tab => tab.Id == tabId))
            {
                if (ActiveTabId != tabId)
                {
                    ActiveTabId = tabId;
                    version++;
                }
            }
        }
    }

    public TerminalTabState? FindTab(Guid tabId)
    {
        lock (syncRoot)
        {
            return tabs.FirstOrDefault(tab => tab.Id == tabId);
        }
    }

    public bool SetDetached(Guid tabId, bool isDetached)
    {
        lock (syncRoot)
        {
            var tab = tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab is null || tab.IsDetached == isDetached)
            {
                return false;
            }

            tab.IsDetached = isDetached;
            version++;
            return true;
        }
    }

    public bool SetAiPanelOpen(Guid tabId, bool isOpen)
    {
        lock (syncRoot)
        {
            var tab = tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab is null || tab.IsAiPanelOpen == isOpen)
            {
                return false;
            }

            tab.IsAiPanelOpen = isOpen;
            version++;
            return true;
        }
    }

    public bool SetAiPanelWidth(Guid tabId, int widthPx)
    {
        lock (syncRoot)
        {
            var tab = tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab is null || tab.AiPanelWidthPx == widthPx)
            {
                return false;
            }

            tab.AiPanelWidthPx = widthPx;
            version++;
            return true;
        }
    }

    public bool SetCopyOnSelect(Guid tabId, bool enabled)
    {
        lock (syncRoot)
        {
            if (tabs.All(item => item.Id != tabId))
            {
                return false;
            }

            var changed = DefaultCopyOnSelect != enabled;
            DefaultCopyOnSelect = enabled;
            foreach (var tab in tabs)
            {
                if (tab.CopyOnSelect != enabled)
                {
                    tab.CopyOnSelect = enabled;
                    changed = true;
                }
            }

            if (!changed)
            {
                return false;
            }

            version++;
            return true;
        }
    }

    public bool SetDefaultCopyOnSelect(bool enabled, bool applyToExisting)
    {
        lock (syncRoot)
        {
            var changed = DefaultCopyOnSelect != enabled;
            DefaultCopyOnSelect = enabled;

            if (applyToExisting)
            {
                foreach (var tab in tabs)
                {
                    if (tab.CopyOnSelect != enabled)
                    {
                        tab.CopyOnSelect = enabled;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                return false;
            }

            version++;
            return true;
        }
    }

    public async Task RemoveTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        TerminalTabState? tab;
        lock (syncRoot)
        {
            var index = tabs.FindIndex(item => item.Id == tabId);
            if (index < 0)
            {
                return;
            }

            tab = tabs[index];
            tabs.RemoveAt(index);

            if (ActiveTabId == tabId)
            {
                ActiveTabId = tabs.ElementAtOrDefault(Math.Max(0, index - 1))?.Id
                    ?? tabs.ElementAtOrDefault(index)?.Id;
            }

            version++;
        }

        tab.RequestDisconnect();
        tab.CancelAiOperation();
        await tab.ConnectionGate.WaitAsync(CancellationToken.None);
        try
        {
            await CloseTabSessionsAsync(tab);
        }
        finally
        {
            tab.ConnectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        TerminalTabState[] tabsToClose;
        lock (syncRoot)
        {
            tabsToClose = tabs.ToArray();
            foreach (var tab in tabsToClose)
            {
                tab.RequestDisconnect();
                tab.CancelAiOperation();
            }
        }

        foreach (var tab in tabsToClose)
        {
            await tab.ConnectionGate.WaitAsync(CancellationToken.None);
            try
            {
                await CloseTabSessionsAsync(tab);
            }
            finally
            {
                tab.ConnectionGate.Release();
            }
        }
    }

    private async Task CloseTabSessionsAsync(TerminalTabState tab)
    {
        var sessionId = tab.SessionId;
        tab.SessionId = null;
        if (tab.Snapshot is not null)
        {
            tab.Snapshot = tab.Snapshot with
            {
                Status = TerminalSessionStatus.Closed,
                LastActivityUtc = DateTimeOffset.UtcNow
            };
        }

        if (sessionId.HasValue)
        {
            await terminalSessionService.CloseSessionAsync(sessionId.Value, CancellationToken.None);
        }

        await terminalSessionService.CloseOwnedSessionsAsync(tab.Id, CancellationToken.None);
    }
}

public sealed class TerminalTabState
{
    private readonly object aiOperationSync = new();
    private readonly object connectionOperationSync = new();
    private CancellationTokenSource? activeAiOperation;
    private CancellationTokenSource? activeConnectionOperation;
    private volatile bool connectionDesired;

    internal SemaphoreSlim ConnectionGate { get; } = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();

    public Guid HostId { get; private set; }

    public string HostName { get; private set; } = string.Empty;

    public string HostAddress { get; private set; } = string.Empty;

    public string DefaultWorkingDirectory { get; private set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public AuthenticationType PrimaryAuthenticationType { get; private set; } = AuthenticationType.Password;

    public AuthenticationType? FallbackAuthenticationType { get; private set; }

    public Guid? SecretHandle { get; set; }

    public bool PreferStoredCredentials { get; set; }

    public bool ConnectionOptionsOpen { get; set; }

    public string ConnectionProfileKey { get; set; } = ConnectionProfileSelectionKeys.HostDefault;

    public string ConnectionProfileName { get; set; } = string.Empty;

    public bool IsDetached { get; set; }

    public bool IsAiPanelOpen { get; set; }

    public TerminalAiAccessMode AiAccessMode { get; set; } = TerminalAiAccessMode.Agent;

    public Guid? AiCommandApprovalSessionId { get; set; }

    public bool AllowInternetResearch { get; set; }

    public int AiPanelWidthPx { get; set; } = 736;

    public bool CopyOnSelect { get; set; }

    private string? workingDirectoryOverride;

    public TerminalAiConversationState AiConversation { get; } = new();

    public Guid? SessionId { get; set; }

    public TerminalSessionSnapshot? Snapshot { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    public bool IsBusy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

    public bool IsSessionActive => Snapshot?.Status == TerminalSessionStatus.Active;

    internal bool ConnectionDesired => connectionDesired;

    public string WorkingDirectory => Snapshot?.WorkingDirectory ?? DefaultWorkingDirectory;

    internal void RequestConnection() => connectionDesired = true;

    internal void RequestDisconnect()
    {
        connectionDesired = false;
        CancellationTokenSource? cancellationSource;
        lock (connectionOperationSync)
        {
            cancellationSource = activeConnectionOperation;
        }

        try
        {
            cancellationSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CancelAiOperation();
    }

    internal void RegisterConnectionOperation(CancellationTokenSource cancellationSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationSource);
        lock (connectionOperationSync)
        {
            activeConnectionOperation = cancellationSource;
        }
    }

    internal void UnregisterConnectionOperation(CancellationTokenSource cancellationSource)
    {
        lock (connectionOperationSync)
        {
            if (ReferenceEquals(activeConnectionOperation, cancellationSource))
            {
                activeConnectionOperation = null;
            }
        }
    }

    internal void RegisterAiOperation(CancellationTokenSource cancellationSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationSource);
        lock (aiOperationSync)
        {
            activeAiOperation = cancellationSource;
        }
    }

    internal void UnregisterAiOperation(CancellationTokenSource cancellationSource)
    {
        lock (aiOperationSync)
        {
            if (ReferenceEquals(activeAiOperation, cancellationSource))
            {
                activeAiOperation = null;
            }
        }
    }

    internal bool CancelAiOperation()
    {
        CancellationTokenSource? cancellationSource;
        lock (aiOperationSync)
        {
            cancellationSource = activeAiOperation;
        }

        if (cancellationSource is null)
        {
            return false;
        }

        try
        {
            cancellationSource.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static TerminalTabState Create(ManagedHost host)
    {
        var tab = new TerminalTabState();
        tab.ApplyHost(host);
        tab.Username = host.Username;
        tab.PreferStoredCredentials =
            !string.IsNullOrWhiteSpace(host.PasswordSecretReference) ||
            !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference);
        tab.ConnectionProfileKey = ConnectionProfileSelectionKeys.HostDefault;
        tab.ConnectionProfileName = host.Username;
        tab.ConnectionOptionsOpen = true;
        return tab;
    }

    public void SetWorkingDirectoryOverride(string? workingDirectory)
    {
        workingDirectoryOverride = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim();
        if (!string.IsNullOrWhiteSpace(workingDirectoryOverride))
        {
            DefaultWorkingDirectory = workingDirectoryOverride;
        }
    }

    public void ApplyHost(ManagedHost host)
    {
        HostId = host.Id;
        HostName = host.Name;
        HostAddress = host.Hostname;
        DefaultWorkingDirectory = workingDirectoryOverride ?? host.DefaultWorkingDirectory;
        PrimaryAuthenticationType = host.PrimaryAuthenticationType;
        FallbackAuthenticationType = host.FallbackAuthenticationType;

        if (string.IsNullOrWhiteSpace(Username))
        {
            Username = host.Username;
        }
    }

    public void ApplyAuthentication(AuthenticationType primaryAuthenticationType, AuthenticationType? fallbackAuthenticationType)
    {
        PrimaryAuthenticationType = primaryAuthenticationType;
        FallbackAuthenticationType = fallbackAuthenticationType;
    }
}
