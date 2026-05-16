// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
            workspace.Tabs.Count(tab => tab.HostId == hostId && tab.SessionId.HasValue));

    public bool HasActiveSession(Guid hostId) =>
        workspaces.Values.Any(workspace =>
            workspace.Tabs.Any(tab =>
                tab.HostId == hostId &&
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
                tab.PreferStoredCredentials))
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
    bool PreferStoredCredentials);

public sealed class TerminalWorkspaceState(ITerminalSessionService terminalSessionService) : IAsyncDisposable
{
    private readonly List<TerminalTabState> tabs = [];
    private readonly object syncRoot = new();
    private long version;

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

        if (tab.SessionId.HasValue)
        {
            await terminalSessionService.CloseSessionAsync(tab.SessionId.Value, cancellationToken);
            tab.SessionId = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Guid[] sessionIds;
        lock (syncRoot)
        {
            sessionIds = tabs
                .Where(tab => tab.SessionId.HasValue)
                .Select(tab => tab.SessionId!.Value)
                .Distinct()
                .ToArray();
        }

        foreach (var sessionId in sessionIds)
        {
            await terminalSessionService.CloseSessionAsync(sessionId);
        }
    }
}

public sealed class TerminalTabState
{
    public Guid Id { get; } = Guid.NewGuid();

    public Guid HostId { get; private set; }

    public string HostName { get; private set; } = string.Empty;

    public string HostAddress { get; private set; } = string.Empty;

    public string HostEnvironment { get; private set; } = string.Empty;

    public string DefaultWorkingDirectory { get; private set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public AuthenticationType PrimaryAuthenticationType { get; private set; } = AuthenticationType.Password;

    public AuthenticationType? FallbackAuthenticationType { get; private set; }

    public Guid? SecretHandle { get; set; }

    public bool PreferStoredCredentials { get; set; }

    public bool ConnectionOptionsOpen { get; set; }

    public bool IsDetached { get; set; }

    public bool IsAiPanelOpen { get; set; }

    public bool SafeInvestigationOnly { get; set; } = true;

    public bool AllowInternetResearch { get; set; }

    public int AiPanelWidthPx { get; set; } = 736;

    private string? workingDirectoryOverride;

    public TerminalAiConversationState AiConversation { get; } = new();

    public Guid? SessionId { get; set; }

    public TerminalSessionSnapshot? Snapshot { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    public bool IsBusy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

    public bool IsSessionActive => Snapshot?.Status == TerminalSessionStatus.Active;

    public string WorkingDirectory => Snapshot?.WorkingDirectory ?? DefaultWorkingDirectory;

    public static TerminalTabState Create(ManagedHost host)
    {
        var tab = new TerminalTabState();
        tab.ApplyHost(host);
        tab.Username = host.Username;
        tab.PreferStoredCredentials =
            !string.IsNullOrWhiteSpace(host.PasswordSecretReference) ||
            !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference);
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
        HostEnvironment = host.Environment;
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
