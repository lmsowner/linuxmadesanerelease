// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
namespace LinuxMadeSane.Web.Services;
public sealed class DesktopHelperRepair(IDesktopShareHost host, IDesktopSessionBroker broker)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> RepairAsync(CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("Desktop Helper repair is already running. Wait for its result.");
        try
        {
            if (host is not DesktopShareHost local) throw new InvalidOperationException("Desktop Helper repair is available on the connected Linux host.");
            var result = await local.ExecuteAsync<JsonElement>("repair", new { }, token);
            var pid = result.GetProperty("processId").GetInt32(); var user = result.GetProperty("user").GetString();
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (broker.GetSnapshot().Sessions.Any(s => s.IsConnected && s.CapabilityReport.ProcessId == pid && s.CapabilityReport.UserName == user && s.CapabilityReport.CanLaunchGuiApps))
                    return $"Desktop Helper repaired and connected for {user}. Its window is opening in the desktop; use View desktop to see it.";
                await Task.Delay(500, token);
            }
            throw new InvalidOperationException("The helper started but has not connected to LMS. Open View desktop and retry Repair Desktop Helper; check the helper log if it still fails.");
        }
        finally { gate.Release(); }
    }
}
