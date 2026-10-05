// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Web.Services;

public sealed class SshMountTerminalTaskLauncher(
    IManagedHostStore hosts, TerminalWorkspaceRegistry registry, TerminalWorkspaceAccessor accessor)
{
    public async Task<string> OpenAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var host = (await hosts.ListAsync(cancellationToken)).FirstOrDefault(item => AiLocalMachine.IsLocalMachine(item.Id))
            ?? throw new InvalidOperationException("The local LMS host is not available in Hosts.");
        var workspace = registry.GetOrCreate(accessor.GetWorkspaceId());
        // A fresh ordinary terminal: no Home Lab tools, protocol or inspection.
        // Existing terminal conversations and task flags are left untouched.
        var tab = workspace.AddTab(host);
        tab.PendingAiTaskPrompt = prompt;
        tab.StartPendingAiTask = true;
        workspace.SetAiPanelOpen(tab.Id, true);
        return $"/hosts/{host.Id}/terminal";
    }
}
