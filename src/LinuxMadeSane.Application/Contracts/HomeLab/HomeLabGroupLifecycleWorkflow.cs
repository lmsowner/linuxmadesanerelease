// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public static class HomeLabGroupLifecycleWorkflow
{
    public static async Task<HomeLabOperationResult> RunAsync(IHomeLabService service, IReadOnlyList<Guid> installationIds,
        HomeLabLifecycleAction action, CancellationToken cancellationToken = default)
    {
        if (action is not (HomeLabLifecycleAction.Start or HomeLabLifecycleAction.Stop or HomeLabLifecycleAction.Restart))
            throw new ArgumentOutOfRangeException(nameof(action));
        await service.RefreshRuntimeHealthAsync(cancellationToken);
        var workspace = await service.GetWorkspaceSnapshotAsync(cancellationToken);
        var ids = installationIds.Distinct().ToHashSet();
        var selected = workspace.Installations.Where(item => ids.Contains(item.Id)).ToArray();
        var output = new List<string>();
        HomeLabOperationResult Result(bool success, string summary) => new(success, summary, string.Join(" ", output), output,
            success ? HomeLabHealthState.Healthy : HomeLabHealthState.Degraded, DateTimeOffset.UtcNow);
        if (ids.Count == 0 || selected.Length != ids.Count) return Result(false, "Group action cancelled: a container is missing. Refresh the group.");
        if (selected.Any(item => item.IsExternallyManaged)) return Result(false, "Group action cancelled: a Docker manager owns a container in this group.");

        var dependencies = workspace.Installations.ToDictionary(item => item.Id, item => Dependencies(item, workspace.Installations));
        if (action is HomeLabLifecycleAction.Stop or HomeLabLifecycleAction.Restart)
        {
            var outsiders = workspace.Installations.Where(item => !ids.Contains(item.Id) && item.HealthState != HomeLabHealthState.Stopped && dependencies[item.Id].Any(ids.Contains)).ToArray();
            if (outsiders.Length > 0)
            {
                output.Add("Containers outside this group depend on it: " + string.Join(", ", outsiders.Select(item => item.DisplayName)) + ". Stop those containers first; their shared dependencies were left running.");
                return Result(false, "Group action blocked by shared dependencies.");
            }
        }
        var ordered = new List<HomeLabAppInstallation>();
        var visiting = new HashSet<Guid>(); var visited = new HashSet<Guid>();
        bool Visit(HomeLabAppInstallation item)
        {
            if (visited.Contains(item.Id)) return true;
            if (!visiting.Add(item.Id)) return false;
            foreach (var id in dependencies[item.Id].Where(ids.Contains)) if (!Visit(selected.Single(x => x.Id == id))) return false;
            visiting.Remove(item.Id); visited.Add(item.Id); ordered.Add(item); return true;
        }
        foreach (var item in selected) if (!Visit(item)) return Result(false, "Group action cancelled: its dependencies contain a cycle.");

        async Task<bool> Execute(HomeLabAppInstallation item, HomeLabLifecycleAction step)
        {
            try
            {
                var result = await service.ExecuteAsync(item.Id, step, cancellationToken);
                output.Add($"{item.DisplayName}: {result.Summary} {result.Detail}");
                return result.Succeeded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { output.Add($"{item.DisplayName}: {ex.Message}"); return false; }
        }
        var failed = new HashSet<Guid>();
        if (action is HomeLabLifecycleAction.Stop or HomeLabLifecycleAction.Restart)
        {
            foreach (var item in ordered.AsEnumerable().Reverse())
            {
                if (failed.Any(id => dependencies[id].Contains(item.Id)))
                { failed.Add(item.Id); output.Add($"{item.DisplayName}: left running because a dependent container could not stop."); continue; }
                if (!await Execute(item, HomeLabLifecycleAction.Stop)) failed.Add(item.Id);
            }
            if (failed.Count > 0) return Result(false, "Group stop incomplete. Review the named containers before restarting.");
            if (action == HomeLabLifecycleAction.Stop) return Result(true, "Group stopped.");
        }

        var ready = new Dictionary<Guid, bool>();
        async Task<bool> EnsureReady(HomeLabAppInstallation item, bool external)
        {
            if (ready.TryGetValue(item.Id, out var available)) return available;
            var current = (await service.GetWorkspaceSnapshotAsync(cancellationToken)).Installations.FirstOrDefault(x => x.Id == item.Id);
            if (current is null) return ready[item.Id] = false;
            if (external && current.HealthState == HomeLabHealthState.Stopped && !current.IsExternallyManaged)
            {
                if (!await Execute(current, HomeLabLifecycleAction.Start)) return ready[item.Id] = false;
            }
            for (var attempt = 0; attempt < 30; attempt++)
            {
                current = (await service.GetWorkspaceSnapshotAsync(cancellationToken)).Installations.FirstOrDefault(x => x.Id == item.Id);
                if (current?.HealthState == HomeLabHealthState.Healthy) return ready[item.Id] = true;
                if (current?.HealthState != HomeLabHealthState.Starting) break;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                await service.RefreshRuntimeHealthAsync(cancellationToken);
            }
            output.Add($"{item.DisplayName}: dependency is not ready ({current?.HealthState}); {current?.HealthDetail}");
            return ready[item.Id] = false;
        }
        foreach (var item in ordered)
        {
            var blocked = false;
            if (item.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase) && !workspace.Installations.Any(x => x.ContainerName == item.NetworkMode["container:".Length..]))
            { output.Add($"{item.DisplayName}: its saved VPN gateway is missing; container was not started."); blocked = true; }
            foreach (var dependency in dependencies[item.Id])
                if (failed.Contains(dependency) || !await EnsureReady(workspace.Installations.Single(x => x.Id == dependency), !ids.Contains(dependency))) blocked = true;
            if (blocked) { failed.Add(item.Id); output.Add($"{item.DisplayName}: not started because a dependency is unavailable."); continue; }
            if (!await Execute(item, HomeLabLifecycleAction.Start)) failed.Add(item.Id);
        }
        return Result(failed.Count == 0, failed.Count == 0 ? action == HomeLabLifecycleAction.Restart ? "Group restarted." : "Group started." : "Group start incomplete. Review the named containers.");
    }

    private static Guid[] Dependencies(HomeLabAppInstallation item, IReadOnlyList<HomeLabAppInstallation> installations)
    {
        var app = HomeLabCatalog.Apps.FirstOrDefault(x => x.Id.Equals(item.AppId, StringComparison.OrdinalIgnoreCase));
        var gateway = item.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase) ? item.NetworkMode["container:".Length..] : null;
        return installations.Where(x => x.Id != item.Id && (gateway == x.ContainerName ||
            x.DeploymentId == item.DeploymentId && (gateway is null || x.AppId != "vpn-gateway") && app?.Dependencies.Contains(x.AppId, StringComparer.OrdinalIgnoreCase) == true)).Select(x => x.Id).ToArray();
    }
}
