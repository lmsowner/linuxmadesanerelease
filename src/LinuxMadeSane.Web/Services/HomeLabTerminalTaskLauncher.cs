// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed class HomeLabTerminalTaskLauncher(IManagedHostService hosts, TerminalWorkspaceRegistry registry, TerminalWorkspaceAccessor accessor)
{
    public async Task<string> OpenAsync(string title, string prompt, CancellationToken cancellationToken = default)
    {
        var host = (await hosts.ListHostsAsync(cancellationToken)).FirstOrDefault(item => AiLocalMachine.IsLocalMachine(item.Id))
            ?? throw new InvalidOperationException("The local LMS host is not available in Hosts.");
        var workspace = registry.GetOrCreate(accessor.GetWorkspaceId());
        var tab = workspace.AddTab(host);
        tab.IsHomeLabTask = true;
        tab.HomeLabTaskTitle = title;
        tab.PendingAiTaskPrompt = prompt;
        tab.StartPendingAiTask = true;
        workspace.SetAiPanelOpen(tab.Id, true);
        return $"/hosts/{host.Id}/terminal";
    }
}
