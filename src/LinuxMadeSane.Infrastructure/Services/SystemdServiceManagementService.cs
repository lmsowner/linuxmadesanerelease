// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SystemdServiceManagementService(ILinuxCommandRunner commandRunner) : IServiceManagementService
{
    public async Task<IReadOnlyList<ServiceState>> InspectAsync(
        IReadOnlyList<string> serviceNames,
        CancellationToken cancellationToken = default)
    {
        var services = new List<ServiceState>();
        foreach (var serviceName in serviceNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "systemctl",
                    ["show", serviceName, "--property=Id,Description,LoadState,ActiveState,UnitFileState", "--no-pager"],
                    false,
                    TimeSpan.FromSeconds(20),
                    $"Inspect {serviceName}"),
                dryRun: false,
                cancellationToken);

            services.Add(ParseServiceState(serviceName, result.StandardOutput));
        }

        return services;
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(
        IReadOnlyList<ServiceAction> actions,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();
        foreach (var action in actions)
        {
            var verb = action.Action switch
            {
                ServiceActionKind.Enable => "enable",
                ServiceActionKind.Disable => "disable",
                ServiceActionKind.Mask => "mask",
                ServiceActionKind.Unmask => "unmask",
                ServiceActionKind.Start => "start",
                ServiceActionKind.Stop => "stop",
                ServiceActionKind.Restart => "restart",
                _ => "status"
            };

            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "systemctl",
                    [verb, action.ServiceName],
                    true,
                    TimeSpan.FromMinutes(2),
                    $"{action.Action} {action.ServiceName}"),
                dryRun,
                cancellationToken);

            logs.Add(new OperationLogEntry(
                result.CompletedAt,
                result.ExitCode == 0 ? OperationLogLevel.Success : OperationLogLevel.Error,
                $"{action.Action} {action.ServiceName}",
                result.CommandText,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError));
        }

        return logs;
    }

    private static ServiceState ParseServiceState(string fallbackName, string output)
    {
        var properties = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var name = Get(properties, "Id", fallbackName);
        var description = Get(properties, "Description", fallbackName);
        var activeState = Get(properties, "ActiveState", "inactive");
        var unitFileState = Get(properties, "UnitFileState", "disabled");
        var loadState = Get(properties, "LoadState", "unknown");

        return new ServiceState(
            name,
            unitFileState.Contains("enabled", StringComparison.OrdinalIgnoreCase),
            activeState.Equals("active", StringComparison.OrdinalIgnoreCase),
            unitFileState.Equals("masked", StringComparison.OrdinalIgnoreCase),
            unitFileState,
            activeState,
            loadState.Equals("not-found", StringComparison.OrdinalIgnoreCase) ? $"{description} (not found)" : description);
    }

    private static string Get(IReadOnlyDictionary<string, string> properties, string key, string fallback) =>
        properties.TryGetValue(key, out var value) ? value : fallback;
}
