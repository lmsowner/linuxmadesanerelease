using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AptPackageManagementService(ILinuxCommandRunner commandRunner) : IPackageManagementService
{
    public async Task<IReadOnlyList<PackageState>> InspectAsync(
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken = default)
    {
        if (packageNames.Count == 0)
        {
            return Array.Empty<PackageState>();
        }

        var packageList = string.Join(' ', packageNames.Select(EscapeShellArgument));
        var script = $"for pkg in {packageList}; do if dpkg-query -W -f='${{Package}}\\t${{Version}}\\t${{db:Status-Status}}\\n' \"$pkg\" 2>/dev/null; then :; else printf '%s\\t-\\tnot-installed\\n' \"$pkg\"; fi; done";
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest("/bin/sh", ["-lc", script], false, TimeSpan.FromSeconds(30), "Inspect package state"),
            dryRun: false,
            cancellationToken);

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParsePackageState)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(
        IReadOnlyList<PackageAction> actions,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        if (actions.Count == 0)
        {
            return logs;
        }

        var needsAptUpdate = actions.Any(action => action.Action is PackageActionKind.Install or PackageActionKind.Remove or PackageActionKind.Reinstall);
        if (needsAptUpdate)
        {
            logs.Add(await RunAndMapAsync(
                new LinuxCommandRequest("apt-get", ["update"], true, TimeSpan.FromMinutes(5), "Refresh package metadata"),
                dryRun,
                cancellationToken));
        }

        foreach (var action in actions)
        {
            var request = action.Action switch
            {
                PackageActionKind.Install => new LinuxCommandRequest("apt-get", ["install", "-y", action.PackageName], true, TimeSpan.FromMinutes(10), $"Install {action.PackageName}"),
                PackageActionKind.Remove => new LinuxCommandRequest("apt-get", ["remove", "-y", action.PackageName], true, TimeSpan.FromMinutes(10), $"Remove {action.PackageName}"),
                PackageActionKind.Reinstall => new LinuxCommandRequest("apt-get", ["install", "--reinstall", "-y", action.PackageName], true, TimeSpan.FromMinutes(10), $"Reinstall {action.PackageName}"),
                _ => new LinuxCommandRequest("/bin/sh", ["-lc", $"dpkg-query -W {EscapeShellArgument(action.PackageName)} >/dev/null 2>&1 || true"], false, TimeSpan.FromSeconds(10), $"Inspect {action.PackageName}")
            };

            logs.Add(await RunAndMapAsync(request, dryRun, cancellationToken));
        }

        return logs;
    }

    private async Task<OperationLogEntry> RunAndMapAsync(
        LinuxCommandRequest request,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(request, dryRun, cancellationToken);
        return new OperationLogEntry(
            result.CompletedAt,
            result.ExitCode == 0 ? OperationLogLevel.Success : OperationLogLevel.Error,
            request.Description,
            result.CommandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    private static PackageState ParsePackageState(string line)
    {
        var parts = line.Split('\t', StringSplitOptions.None);
        var name = parts.ElementAtOrDefault(0) ?? string.Empty;
        var version = parts.ElementAtOrDefault(1) ?? "-";
        var status = parts.ElementAtOrDefault(2) ?? "unknown";
        return new PackageState(name, status.Equals("installed", StringComparison.OrdinalIgnoreCase), version, status);
    }

    private static string EscapeShellArgument(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";
}
