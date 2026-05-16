// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Renci.SshNet;
using System.Globalization;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ManagedHostHealthProbe(
    ManagedHostSshConnectionFactory sshConnectionFactory,
    ILinuxCommandRunner linuxCommandRunner) : IManagedHostHealthProbe
{
    private static readonly TimeSpan RemoteConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RemoteCommandTimeout = TimeSpan.FromSeconds(6);

    private const string HealthScript = """
        read cpu user nice system idle iowait irq softirq steal _ < /proc/stat
        idle1=$((idle + iowait))
        total1=$((user + nice + system + idle + iowait + irq + softirq + steal))
        sleep 0.4
        read cpu user nice system idle iowait irq softirq steal _ < /proc/stat
        idle2=$((idle + iowait))
        total2=$((user + nice + system + idle + iowait + irq + softirq + steal))
        diff_idle=$((idle2 - idle1))
        diff_total=$((total2 - total1))
        if [ "$diff_total" -gt 0 ]; then
          cpu_pct=$(awk -v idle="$diff_idle" -v total="$diff_total" 'BEGIN { printf "%.1f", (100 * (total - idle) / total) }')
        else
          cpu_pct="0.0"
        fi
        printf 'cpu_pct\t%s\n' "$cpu_pct"
        printf 'uptime\t'; (uptime -p 2>/dev/null || uptime)
        printf 'load\t'; cat /proc/loadavg 2>/dev/null
        mem_total_kb=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo 2>/dev/null)
        mem_avail_kb=$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo 2>/dev/null)
        swap_free_kb=$(awk '/^SwapFree:/ {print $2}' /proc/meminfo 2>/dev/null)
        if [ -n "$mem_total_kb" ] && [ "$mem_total_kb" -gt 0 ] && [ -n "$mem_avail_kb" ]; then
          memory_free_pct=$(awk -v available="$mem_avail_kb" -v total="$mem_total_kb" 'BEGIN { printf "%.1f", (available / total) * 100 }')
        else
          memory_free_pct=""
        fi
        printf 'memory_free_pct\t%s\n' "$memory_free_pct"
        printf 'memory_avail_bytes\t%s\n' "${mem_avail_kb:+$((mem_avail_kb * 1024))}"
        printf 'swap_free_bytes\t%s\n' "${swap_free_kb:+$((swap_free_kb * 1024))}"
        printf 'root_disk_pct\t'; df -Pk / | awk 'NR==2 {gsub("%","",$5); print $5}'
        printf 'root_disk_avail\t'; df -hP / | awk 'NR==2 {print $4}'
        """;

    public async Task<ServerHealthSnapshot> GetSnapshotAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return await GetLocalSnapshotAsync(cancellationToken);
        }

        try
        {
            return await GetRemoteSnapshotAsync(host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return BuildUnavailableSnapshot(DateTimeOffset.UtcNow);
        }
    }

    private async Task<ServerHealthSnapshot> GetLocalSnapshotAsync(CancellationToken cancellationToken)
    {
        var result = await linuxCommandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", HealthScript],
                false,
                RemoteCommandTimeout,
                "Collect local host health")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        return result.ExitCode == 0
            ? ParseSnapshot(result.StandardOutput, result.CompletedAt)
            : BuildUnavailableSnapshot(result.CompletedAt);
    }

    private async Task<ServerHealthSnapshot> GetRemoteSnapshotAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var credentials = await sshConnectionFactory.ResolveStoredCredentialsAsync(host, cancellationToken);
        using var client = sshConnectionFactory.CreateSshClient(
            host,
            credentials,
            RemoteConnectTimeout,
            TimeSpan.FromSeconds(15));

        client.Connect();

        try
        {
            using var command = client.CreateCommand(WrapShellScript(HealthScript));
            command.CommandTimeout = RemoteCommandTimeout;

            var output = command.Execute();
            var completedAt = DateTimeOffset.UtcNow;

            return command.ExitStatus == 0
                ? ParseSnapshot(output ?? string.Empty, completedAt)
                : BuildUnavailableSnapshot(completedAt);
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private static ServerHealthSnapshot ParseSnapshot(string output, DateTimeOffset capturedAtUtc)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length > 0)
            .ToDictionary(
                parts => parts[0],
                parts => parts.ElementAtOrDefault(1) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        return new ServerHealthSnapshot(
            TryParseDouble(lines.GetValueOrDefault("cpu_pct")),
            TryParseDouble(lines.GetValueOrDefault("memory_free_pct")),
            TryParseLong(lines.GetValueOrDefault("memory_avail_bytes")),
            TryParseLong(lines.GetValueOrDefault("swap_free_bytes")),
            TryParseDouble(lines.GetValueOrDefault("root_disk_pct")),
            lines.GetValueOrDefault("root_disk_avail", string.Empty),
            NormalizeLoadAverage(lines.GetValueOrDefault("load")),
            lines.GetValueOrDefault("uptime", "Unavailable"),
            capturedAtUtc);
    }

    private static ServerHealthSnapshot BuildUnavailableSnapshot(DateTimeOffset capturedAtUtc) =>
        new(
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            "Unavailable",
            "Unavailable",
            capturedAtUtc);

    private static double? TryParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static long? TryParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string NormalizeLoadAverage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unavailable";
        }

        return string.Join(
            " ",
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
    }

    private static string WrapShellScript(string script) =>
        $"bash -lc {QuoteShellArgument(script)}";

    private static string QuoteShellArgument(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";
}
