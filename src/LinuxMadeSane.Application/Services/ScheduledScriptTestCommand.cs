// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Application.Contracts.Scheduling;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Services;

public static class ScheduledScriptTestCommand
{
    public static LinuxCommandRequest Build(Guid runId, string path, string arguments, string workingDirectory, string runAsUser)
    {
        var user = runAsUser.Trim();
        if (user.Length == 0 || user.StartsWith('-') || user.Any(char.IsWhiteSpace) || user.Any(char.IsControl))
            throw new ValidationException("Select the Linux user that should run this script.");
        var command = ScheduledScriptExecution.BuildCommand(new("Script", path, arguments, workingDirectory), user);
        // A transient systemd unit provides the existing privileged runner with
        // streamed output and reliable cancellation of the entire process group.
        // It creates no saved task, cron entry, or persistent service definition.
        return new LinuxCommandRequest("systemd-run",
            ["--quiet", "--pipe", "--wait", "--collect", $"--unit={UnitName(runId)}", $"--uid={user}",
             "--property=RuntimeMaxSec=300s", "--property=TimeoutStopSec=5s", "--property=KillMode=control-group",
             "--", "bash", "-lc", command],
            true, TimeSpan.FromMinutes(6), $"Test script as {user}", "/")
        { SensitiveArgumentIndexes = new HashSet<int> { 12 } };
    }

    public static string UnitName(Guid runId) => $"lms-script-test-{runId:N}.service";
}
