// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public static class ServiceHealthAssessment
{
    public static (ServiceHealthStatus Status, string Reason) Assess(
        string activeState, string loadState = "loaded", string subState = "", string result = "")
    {
        if (loadState is "error" or "bad-setting" or "not-found")
            return (ServiceHealthStatus.Broken, $"systemd cannot load this unit ({loadState}).");
        if (activeState == "failed" || subState == "auto-restart")
            return (ServiceHealthStatus.Broken, $"systemd reports {activeState}/{subState}. " +
                (string.IsNullOrWhiteSpace(result) ? "Open Info for the result and exit code." : $"Result: {result}."));
        if (!string.IsNullOrWhiteSpace(result) && result is not ("success" or "done"))
            return (ServiceHealthStatus.Warning, $"The last service operation returned {result}; current state is {activeState}/{subState}.");
        return activeState switch
        {
            "active" when subState == "exited" => (ServiceHealthStatus.Healthy, "Completed successfully; this unit does not need a continuously running process."),
            "active" => (ServiceHealthStatus.Healthy, $"systemd reports {activeState}/{subState}; no current failure is reported."),
            "inactive" => (ServiceHealthStatus.Healthy, "Not running. Inactive/on-demand services are normal; systemd reports no current failure."),
            "activating" => (ServiceHealthStatus.Warning, $"Service is starting ({subState}). Refresh to check whether it completes."),
            "deactivating" => (ServiceHealthStatus.Warning, $"Service is stopping ({subState}). Refresh to check whether it completes."),
            _ => (ServiceHealthStatus.Warning, $"Current systemd state is unavailable or unrecognised ({activeState}/{subState}).")
        };
    }
}
