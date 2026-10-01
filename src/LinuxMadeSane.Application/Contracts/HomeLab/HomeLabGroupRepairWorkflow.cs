// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public static class HomeLabGroupRepairWorkflow
{
    public static async Task<HomeLabOperationResult> RunAsync(IHomeLabService service, IReadOnlyList<Guid> installationIds, CancellationToken cancellationToken = default)
    {
        var ids = installationIds.Distinct().ToHashSet();
        await service.RefreshRuntimeHealthAsync(cancellationToken);
        var workspace = await service.GetWorkspaceSnapshotAsync(cancellationToken);
        var selected = workspace.Installations.Where(item => ids.Contains(item.Id)).ToArray();
        var output = new List<string>();
        var failures = 0;
        var repaired = new HashSet<Guid>();
        var unavailableGateways = new HashSet<string>(StringComparer.Ordinal);
        var gatewayNames = selected.Where(item => item.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.NetworkMode["container:".Length..]).Distinct(StringComparer.Ordinal).ToArray();

        // One shared fault must not trigger repeated independent reconstructions.
        foreach (var name in gatewayNames)
        {
            var gateway = workspace.Installations.FirstOrDefault(item => item.ContainerName == name);
            if (gateway is null)
            {
                unavailableGateways.Add(name);
                output.Add($"VPN gateway {name} is missing; its dependent containers were left intact.");
                continue;
            }
            if (gateway.HealthState is HomeLabHealthState.Healthy) continue;
            if (gateway.IsExternallyManaged || gateway.HealthState == HomeLabHealthState.Stopped)
            {
                unavailableGateways.Add(name);
                output.Add($"{gateway.DisplayName}: gateway is {gateway.HealthState}; start it or use its Docker manager before repairing dependents.");
                continue;
            }
            var result = await RepairOrWaitAsync(service, gateway, cancellationToken);
            output.Add($"{gateway.DisplayName}: {result.Detail}");
            if (!result.Succeeded) unavailableGateways.Add(name);
            else repaired.Add(gateway.Id);
        }

        // A gateway repair may already have recovered every routed container.
        await service.RefreshRuntimeHealthAsync(cancellationToken);
        workspace = await service.GetWorkspaceSnapshotAsync(cancellationToken);
        foreach (var id in ids)
        {
            var installation = workspace.Installations.FirstOrDefault(item => item.Id == id);
            if (installation is null) { failures++; output.Add($"Installation {id}: saved record is missing."); continue; }
            if (installation.HealthState == HomeLabHealthState.Healthy)
            {
                output.Add($"{installation.DisplayName}: healthy; no recreation needed.");
                continue;
            }
            if (installation.HealthState == HomeLabHealthState.Stopped || installation.IsExternallyManaged)
            {
                if (installation.IsExternallyManaged) failures++;
                output.Add($"{installation.DisplayName}: left {installation.HealthState}; managed lifecycle was not changed.");
                continue;
            }
            if (installation.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase) &&
                unavailableGateways.Contains(installation.NetworkMode["container:".Length..]))
            {
                failures++;
                output.Add($"{installation.DisplayName}: blocked by its VPN gateway; saved configuration retained.");
                continue;
            }
            var result = await RepairOrWaitAsync(service, installation, cancellationToken);
            output.Add($"{installation.DisplayName}: {result.Detail}");
            if (!result.Succeeded) failures++;
            else repaired.Add(id);
            workspace = await service.GetWorkspaceSnapshotAsync(cancellationToken);
        }
        var detail = string.Join(" ", output);
        return new HomeLabOperationResult(failures == 0,
            failures == 0 ? $"Group checked; {repaired.Count} container(s) repaired and verified." : $"Group repair incomplete: {failures} container(s) still need attention.",
            detail, output, failures == 0 ? HomeLabHealthState.Healthy : HomeLabHealthState.Degraded, DateTimeOffset.UtcNow);
    }

    private static async Task<HomeLabOperationResult> RepairOrWaitAsync(IHomeLabService service, HomeLabAppInstallation installation, CancellationToken cancellationToken)
    {
        var state = installation.HealthState;
        HomeLabOperationResult? repair = null;
        try
        {
            if (state != HomeLabHealthState.Starting)
            {
                repair = await service.ExecuteAsync(installation.Id, HomeLabLifecycleAction.Repair, cancellationToken);
                if (!repair.Succeeded) return repair;
                state = repair.HealthState;
            }
            for (var attempt = 0; state == HomeLabHealthState.Starting && attempt < 60; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var refreshed = await service.RefreshRuntimeHealthAsync(cancellationToken);
                state = refreshed.FirstOrDefault(item => item.InstallationId == installation.Id)?.HealthState ?? HomeLabHealthState.Failed;
            }
            var current = (await service.GetWorkspaceSnapshotAsync(cancellationToken)).Installations.FirstOrDefault(item => item.Id == installation.Id);
            return new HomeLabOperationResult(current?.HealthState == HomeLabHealthState.Healthy,
                current?.HealthState == HomeLabHealthState.Healthy ? "Repair verified." : "Repair did not restore healthy operation.", current?.HealthDetail ?? "Saved installation is missing.", repair?.Output ?? [], current?.HealthState ?? HomeLabHealthState.Failed, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HomeLabOperationResult(false, "Repair failed.", exception.Message, [], HomeLabHealthState.Failed, DateTimeOffset.UtcNow);
        }
    }
}
