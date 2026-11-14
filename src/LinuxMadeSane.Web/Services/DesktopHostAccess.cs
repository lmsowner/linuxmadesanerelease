// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
namespace LinuxMadeSane.Web.Services;

public sealed record DesktopHostAccessStatus(bool HasDesktop, bool Ready, int Port, string Message, bool Preparing = false);
public sealed record DesktopHostAccessRequest(string Token, bool Prepare);

public sealed class DesktopHostAccess(IDesktopInspectionService inspection, DesktopSetupOperations operations, IServiceManagementService services)
{
    public async Task<DesktopHostAccessStatus> GetAsync(bool prepare, CancellationToken token = default)
    {
        var report = await inspection.InspectAsync(token);
        var hasDesktop = report.InstalledDesktopEnvironments.Count > 0;
        if (!hasDesktop) return new(false, false, 0, "This host has no graphical desktop installed. Install a desktop in Desktop Assistant > Desktops first.");
        var existingLauncher = File.Exists("/etc/xrdp/startwm.sh");
        var ready = report.XrdpInstalled && report.XrdpServiceActive && existingLauncher;
        var state = operations.Current;
        if (state.Running) return new(true, false, 0, state.Message, true);
        if (!ready && prepare && report.XrdpInstalled && existingLauncher)
        {
            // A working custom XRDP setup belongs to the user. Start it without replacing its desktop or boot policy.
            if (report.Services.Any(service => service.Name == "xrdp.service" && service.IsMasked))
                throw new InvalidOperationException("XRDP is masked by the administrator. Its disabled policy has been preserved.");
            var logs = await services.ApplyActionsAsync([
                new(ServiceActionKind.Start, "xrdp-sesman.service", "Start the existing desktop session manager.", false, "systemctl start xrdp-sesman.service"),
                new(ServiceActionKind.Start, "xrdp.service", "Start the existing remote desktop listener.", false, "systemctl start xrdp.service")], false, token);
            if (logs.Any(log => log.Level == OperationLogLevel.Error))
                throw new InvalidOperationException("The existing remote desktop service could not start. " + string.Join(" ", logs.Where(log => log.Level == OperationLogLevel.Error).Select(log => log.StandardError)));
            report = await inspection.InspectAsync(token);
            ready = report.XrdpServiceActive;
            if (!ready) throw new InvalidOperationException("The existing desktop service did not become active. Its configuration was preserved.");
        }
        if (!ready && prepare)
        {
            operations.Start(new() { Profile = RdpOptimizationProfile.RdpOptimizedXfce, ConfigureRemoteDesktop = true,
                ConfigureDesktop = false, BindNewXrdpToLoopback = true, CreateSnapshotBeforeChanges = true });
            return new(true, false, 0, operations.Current.Message, true);
        }
        var config = File.Exists("/etc/xrdp/xrdp.ini") ? await File.ReadAllTextAsync("/etc/xrdp/xrdp.ini", token) : "";
        var port = report.XrdpInstalled ? XrdpConfiguration.Port(config) : 0;
        return new(true, ready, port, ready ? "Remote desktop is ready." : state.Result?.Success == false ? "Desktop preparation failed. " + string.Join(" ", state.Result.OperationLogs.Where(log => log.Level == OperationLogLevel.Error).Select(log => log.StandardError ?? log.Message)) : "Remote desktop preparation has not completed.");
    }
}
