// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.Storage;

public static class StoragePrivilegeChecks
{
    private const string MissingHelper = "This LMS installation is missing its privileged storage helper. Update or reinstall LMS before retrying this operation. No storage commands were run.";

    public static void ValidateEmbeddedResources() => _ = ReadScript();
    private static string ReadScript()
    {
        using var stream = typeof(StoragePrivilegeChecks).Assembly.GetManifestResourceStream(
            "LinuxMadeSane.Infrastructure.Services.Storage.privileged_storage.py")
            ?? throw new InvalidOperationException(MissingHelper);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static Task<LinuxCommandResult> RunAsync(ILinuxCommandRunner runner, string mode,
        IReadOnlyList<string> arguments, CancellationToken token = default)
    {
        string script;
        try { script = ReadScript(); }
        catch (InvalidOperationException error)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new LinuxCommandResult("Load privileged storage helper", 1, "",
                error.Message, now, now, false));
        }
        return runner.RunAsync(new LinuxCommandRequest("python3", ["-c", script, mode, .. arguments], true,
            TimeSpan.FromSeconds(30), "Privileged storage " + mode), false, token);
    }

    internal static async Task<StorageValidationResult> ValidateAsync(ILinuxCommandRunner runner,
        StorageResizePlan plan, CancellationToken token = default)
    {
        try
        {
            var check = await RunAsync(runner, plan.Steps.Any(s => s.Operation == StorageResizeStepOperation.UpdateFstab)
                ? "fstab-check" : "privilege-check", ["/etc/fstab", plan.TargetMountPoint], token);
            if (check.ExitCode != 0) return new("Elevated storage access", false, Detail(check));
            if (!LinuxCommandRunner.IsCurrentProcessRoot())
            {
                if (plan.Steps.Any(s => s.Operation == StorageResizeStepOperation.UpdateFstab))
                {
                    var uuidAccess = await runner.RunAsync(new LinuxCommandRequest("sudo",
                        ["-n", "-l", "--", "blkid", "-s", "UUID", "-o", "value", plan.TargetDevicePath], false,
                        TimeSpan.FromSeconds(10), "Check sudo authorization for UUID lookup"), false, token);
                    if (uuidAccess.ExitCode != 0) return new("Elevated storage access", false, Detail(uuidAccess));
                }
                foreach (var step in plan.Steps.Where(s => s.State != StorageStepState.Completed && !string.IsNullOrWhiteSpace(s.Executable)))
                {
                    var allowed = await runner.RunAsync(new LinuxCommandRequest("sudo",
                        ["-n", "-l", "--", step.Executable!, .. step.Arguments], false, TimeSpan.FromSeconds(10),
                        "Check sudo authorization for " + step.Title), false, token);
                    if (allowed.ExitCode != 0) return new("Elevated storage access", false, step.Title + ": " + Detail(allowed));
                }
            }
            return new("Elevated storage access", true, "Privileged execution and required persistence access verified before disk changes.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new("Elevated storage access", false, error.Message); }
    }

    internal static string Detail(LinuxCommandResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
}
