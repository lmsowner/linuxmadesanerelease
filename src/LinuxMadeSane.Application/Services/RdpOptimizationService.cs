// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.RdpOptimizer;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Services;

public sealed class RdpOptimizationService(
    IDesktopInspectionService desktopInspectionService,
    IPackageManagementService packageManagementService,
    IServiceManagementService serviceManagementService,
    ISessionConfigurationService sessionConfigurationService,
    IRestoreSnapshotService restoreSnapshotService) : IRdpOptimizationService
{
    private static readonly string[] KnownDisplayManagers =
    [
        "gdm.service",
        "gdm3.service",
        "lightdm.service",
        "sddm.service"
    ];

    public async Task<RdpOptimizerWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var inspection = await desktopInspectionService.InspectAsync(cancellationToken);
        var snapshots = (await restoreSnapshotService.ListSnapshotsAsync(cancellationToken)).ToArray();
        var runs = (await restoreSnapshotService.ListRunResultsAsync(cancellationToken)).ToArray();
        var currentModeLabel = DetermineCurrentModeLabel(inspection);

        return new RdpOptimizerWorkspaceViewModel(
            inspection,
            currentModeLabel,
            BuildCurrentModeSummary(inspection, currentModeLabel),
            new RdpOptimizationRequestEditor
            {
                Profile = DetermineSuggestedProfile(inspection)
            },
            BuildModeOptions(inspection),
            BuildRecommendations(inspection),
            snapshots.FirstOrDefault(),
            runs.FirstOrDefault(),
            snapshots,
            runs);
    }

    public async Task<RdpOptimizerOverviewViewModel> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceAsync(cancellationToken);
        return new RdpOptimizerOverviewViewModel(
            workspace.Inspection,
            workspace.Editor.Profile,
            workspace.LatestSnapshot,
            workspace.LatestRun,
            workspace.Recommendations);
    }

    public async Task<RdpOptimizerInspectViewModel> GetInspectAsync(CancellationToken cancellationToken = default) =>
        new(await desktopInspectionService.InspectAsync(cancellationToken));

    public async Task<RdpOptimizerOptimizeViewModel> GetOptimizeAsync(CancellationToken cancellationToken = default)
    {
        var inspection = await desktopInspectionService.InspectAsync(cancellationToken);
        var latestRun = (await restoreSnapshotService.ListRunResultsAsync(cancellationToken)).FirstOrDefault();

        return new RdpOptimizerOptimizeViewModel(
            inspection,
            new RdpOptimizationRequestEditor
            {
                Profile = DetermineSuggestedProfile(inspection)
            },
            Array.Empty<string>(),
            null,
            latestRun);
    }

    public async Task<RdpOptimizationPlan> BuildPlanAsync(
        RdpOptimizationRequestEditor editor,
        CancellationToken cancellationToken = default)
    {
        var request = Map(editor);
        var inspection = await desktopInspectionService.InspectAsync(cancellationToken);

        if (request.InspectOnly || request.Profile == RdpOptimizationProfile.Default)
        {
            return BuildInspectOnlyPlan(request, inspection);
        }

        return request.Profile switch
        {
            RdpOptimizationProfile.RestoreFullDesktop => await BuildRestorePlanAsync(request, inspection, cancellationToken),
            RdpOptimizationProfile.HeadlessConsole => await BuildHeadlessPlanAsync(request, inspection, cancellationToken),
            RdpOptimizationProfile.GnomeDesktop => await BuildDesktopPlanAsync(
                request,
                inspection,
                RdpOptimizerCatalog.RequiredGnomePackages,
                ResolveDisplayManagerServiceName(inspection.Services, "gdm3.service", "gdm.service"),
                "GNOME",
                cancellationToken),
            RdpOptimizationProfile.KdePlasmaDesktop => await BuildDesktopPlanAsync(
                request,
                inspection,
                RdpOptimizerCatalog.RequiredKdePackages,
                ResolveDisplayManagerServiceName(inspection.Services, "sddm.service"),
                "KDE Plasma",
                cancellationToken),
            RdpOptimizationProfile.RdpOptimizedXfce => await BuildXfcePlanAsync(request, inspection, cancellationToken),
            _ => BuildInspectOnlyPlan(request, inspection)
        };
    }

    public async Task<RdpOptimizationResult> ExecuteAsync(
        RdpOptimizationRequestEditor editor,
        CancellationToken cancellationToken = default)
    {
        var request = Map(editor);
        var plan = await BuildPlanAsync(editor, cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var logs = new List<OperationLogEntry>();
        RestoreSnapshot? snapshot = null;
        var hasPlannedChanges = !plan.InspectOnly && plan.PackageActions.Count + plan.ServiceActions.Count + plan.SessionChanges.Count > 0;

        if (request.DryRun && hasPlannedChanges && request.CreateSnapshotBeforeChanges)
        {
            logs.Add(new OperationLogEntry(
                DateTimeOffset.UtcNow,
                OperationLogLevel.Info,
                "Dry-run skips snapshot creation. A snapshot would be taken before applying this plan.",
                null,
                0,
                null,
                null));
        }
        else if (hasPlannedChanges && request.CreateSnapshotBeforeChanges)
        {
            try
            {
                snapshot = await restoreSnapshotService.CreateSnapshotAsync(
                    plan.Profile,
                    plan.Inspection,
                    CollectFilesToBackup(plan),
                    cancellationToken);

                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Success,
                    $"Created restore snapshot {snapshot.SnapshotId:N}.",
                    null,
                    0,
                    null,
                    null));
            }
            catch (Exception exception)
            {
                logs.Add(new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Error,
                    "Failed to create a restore snapshot. No changes were applied.",
                    null,
                    -1,
                    null,
                    exception.Message));

                var failedResult = new RdpOptimizationResult(
                    Guid.NewGuid(),
                    plan.Profile,
                    false,
                    false,
                    false,
                    null,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    plan,
                    plan.Inspection,
                    logs,
                    null,
                    plan.Warnings
                        .Append("Snapshot creation failed, so the desktop mode change stopped before changing the machine.")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray());

                await restoreSnapshotService.SaveRunResultAsync(failedResult, cancellationToken);
                return failedResult;
            }
        }
        else if (hasPlannedChanges)
        {
            logs.Add(new OperationLogEntry(
                DateTimeOffset.UtcNow,
                OperationLogLevel.Warning,
                "Snapshot creation was disabled. Rolling back will be more manual if something goes wrong.",
                null,
                null,
                null,
                null));
        }

        if (request.DryRun || plan.InspectOnly)
        {
            var dryRunReport = new DryRunReport(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                plan.Profile,
                CollectPlannedCommands(plan),
                plan.Warnings,
                logs);

            var dryRunResult = new RdpOptimizationResult(
                Guid.NewGuid(),
                plan.Profile,
                true,
                request.DryRun,
                plan.InspectOnly,
                snapshot?.SnapshotId,
                startedAt,
                DateTimeOffset.UtcNow,
                plan,
                plan.Inspection,
                logs,
                dryRunReport,
                plan.Warnings);

            await restoreSnapshotService.SaveRunResultAsync(dryRunResult, cancellationToken);
            return dryRunResult;
        }

        if (plan.Profile == RdpOptimizationProfile.RestoreFullDesktop)
        {
            snapshot = await ResolveSnapshotAsync(request, cancellationToken);
            if (snapshot is null)
            {
                var failedInspection = await desktopInspectionService.InspectAsync(cancellationToken);
                var missingSnapshotResult = new RdpOptimizationResult(
                    Guid.NewGuid(),
                    plan.Profile,
                    false,
                    false,
                    false,
                    null,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    plan,
                    failedInspection,
                    [
                        new OperationLogEntry(
                            DateTimeOffset.UtcNow,
                            OperationLogLevel.Error,
                            "Restore was requested but no matching snapshot was found.",
                            null,
                            -1,
                            null,
                            null)
                    ],
                    null,
                    plan.Warnings);

                await restoreSnapshotService.SaveRunResultAsync(missingSnapshotResult, cancellationToken);
                return missingSnapshotResult;
            }

            var packageActionsBeforeServiceRestore = plan.PackageActions
                .Where(action => action.Action is not PackageActionKind.Remove)
                .ToArray();
            var packageActionsAfterServiceRestore = plan.PackageActions
                .Where(action => action.Action is PackageActionKind.Remove)
                .ToArray();

            if (packageActionsBeforeServiceRestore.Length > 0)
            {
                logs.AddRange(await packageManagementService.ApplyActionsAsync(packageActionsBeforeServiceRestore, dryRun: false, cancellationToken));
            }

            logs.AddRange(await sessionConfigurationService.RestoreAsync(snapshot, dryRun: false, cancellationToken));
            logs.AddRange(await serviceManagementService.ApplyActionsAsync(plan.ServiceActions, dryRun: false, cancellationToken));

            if (packageActionsAfterServiceRestore.Length > 0)
            {
                logs.AddRange(await packageManagementService.ApplyActionsAsync(packageActionsAfterServiceRestore, dryRun: false, cancellationToken));
            }
        }
        else
        {
            var runtimeMaskOwned = request.BindNewXrdpToLoopback && !plan.Inspection.XrdpInstalled;
            try
            {
                if (request.BindNewXrdpToLoopback && !plan.Inspection.XrdpInstalled)
                    logs.AddRange(await serviceManagementService.ApplyActionsAsync([new(ServiceActionKind.Mask, "xrdp.service", "Prevent a new XRDP service from listening before its private configuration is ready.", false, "systemctl mask --runtime xrdp.service") { RuntimeOnly = true }], false, cancellationToken));
                if (logs.All(log => log.Level != OperationLogLevel.Error))
                    logs.AddRange(await packageManagementService.ApplyActionsAsync(plan.PackageActions, dryRun: false, cancellationToken));
                if (logs.All(log => log.Level != OperationLogLevel.Error))
                    logs.AddRange(await sessionConfigurationService.ApplyOptimizationChangesAsync(plan.SessionChanges, false, dryRun: false, cancellationToken));
                if (logs.All(log => log.Level != OperationLogLevel.Error))
                    logs.AddRange(await serviceManagementService.ApplyActionsAsync(plan.ServiceActions, dryRun: false, cancellationToken));
                if (logs.All(log => log.Level != OperationLogLevel.Error) && request.ConfigureRemoteDesktop &&
                    plan.SessionChanges.Any(change => change.FilePath == "/etc/xrdp/startwm.sh"))
                {
                    var configured = await desktopInspectionService.InspectAsync(cancellationToken);
                    if (!configured.XrdpServiceActive)
                        logs.Add(new(DateTimeOffset.UtcNow, OperationLogLevel.Error, "XRDP did not become active. The remote desktop setup was not completed.", null, -1, null, null));
                }
            }
            catch (Exception error)
            {
                logs.Add(new(DateTimeOffset.UtcNow, OperationLogLevel.Error, "Desktop setup did not complete.", null, -1, null, error.Message));
            }
            if (runtimeMaskOwned)
                logs.AddRange(await serviceManagementService.ApplyActionsAsync([new(ServiceActionKind.Unmask, "xrdp.service", "Remove the temporary first-install start guard.", false, "systemctl unmask --runtime xrdp.service") { RuntimeOnly = true }], false, CancellationToken.None));
            if (logs.Any(log => log.Level == OperationLogLevel.Error) && snapshot is not null)
            {
                logs.AddRange(await sessionConfigurationService.RestoreAsync(snapshot, false, CancellationToken.None));
                var current = await desktopInspectionService.InspectAsync(CancellationToken.None);
                var touchedServices = plan.ServiceActions.Select(action => action.ServiceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // Packages may have started XRDP during installation, so include its original service state.
                if (request.ConfigureRemoteDesktop) { touchedServices.Add("xrdp.service"); touchedServices.Add("xrdp-sesman.service"); }
                logs.AddRange(await serviceManagementService.ApplyActionsAsync(
                    BuildRestoreServiceActions(snapshot.RelevantServices.Where(service => touchedServices.Contains(service.Name)).ToArray(), current.Services), false, CancellationToken.None));
                logs.Add(new(DateTimeOffset.UtcNow, OperationLogLevel.Warning,
                    "Previous session configuration and service state restoration attempted. Newly installed packages are retained; see the restore log for any errors.", null, null, null, null));
            }
        }

        var postInspection = await desktopInspectionService.InspectAsync(cancellationToken);
        if (snapshot is not null && plan.Profile is not RdpOptimizationProfile.RestoreFullDesktop)
        {
            var updatedSnapshot = snapshot with
            {
                RemovedPackages = plan.PackageActions
                    .Where(action => action.Action == PackageActionKind.Remove)
                    .Select(action => action.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Notes = snapshot.Notes
                    .Concat(plan.Warnings)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            await restoreSnapshotService.UpdateSnapshotAsync(updatedSnapshot, cancellationToken);
            snapshot = updatedSnapshot;
        }

        var success = logs.All(log => log.Level is not OperationLogLevel.Error);
        var result = new RdpOptimizationResult(
            Guid.NewGuid(),
            plan.Profile,
            success,
            false,
            false,
            snapshot?.SnapshotId,
            startedAt,
            DateTimeOffset.UtcNow,
            plan,
            postInspection,
            logs,
            null,
            plan.Warnings);

        await restoreSnapshotService.SaveRunResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<RdpOptimizerHistoryViewModel> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        new(
            await restoreSnapshotService.ListSnapshotsAsync(cancellationToken),
            await restoreSnapshotService.ListRunResultsAsync(cancellationToken));

    public Task<RdpOptimizationResult> RestoreAsync(
        Guid snapshotId,
        bool reinstallRemovedPackages,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new RdpOptimizationRequestEditor
            {
                Profile = RdpOptimizationProfile.RestoreFullDesktop,
                SnapshotIdToRestore = snapshotId,
                RestoreRemovedPackages = reinstallRemovedPackages,
                DryRun = dryRun,
                CreateSnapshotBeforeChanges = !dryRun
            },
            cancellationToken);

    private async Task<RdpOptimizationPlan> BuildXfcePlanAsync(
        RdpOptimizationRequest request,
        DesktopInspectionReport inspection,
        CancellationToken cancellationToken)
    {
        var packageActions = new List<PackageAction>();
        var serviceActions = new List<ServiceAction>();
        var warnings = new List<string>();
        if (request.BindNewXrdpToLoopback && inspection.Services.Any(service => service.Name == "xrdp.service" && service.IsMasked))
            throw new InvalidOperationException("XRDP is masked by the administrator. Its disabled policy has been preserved.");

        if (request.ConfigureRemoteDesktop)
            foreach (var packageName in RdpOptimizerCatalog.RequiredXrdpPackages)
                AddInstallActionIfMissing(packageActions, inspection.Packages, packageName, request.InstallXrdpIfMissing, warnings, "Remote desktop requires this package.");
        foreach (var packageName in RdpOptimizerCatalog.RequiredXfcePackages)
            AddInstallActionIfMissing(packageActions, inspection.Packages, packageName, request.InstallXfceIfMissing, warnings, "XFCE requires this package.");
        if (request.ConfigureDesktop && (request.RepairDesktop || DesktopSessionCatalog.NeedsDisplayManager(request.Profile, inspection.SessionConfiguration.DisplayManager)))
        {
            AddInstallActionIfMissing(packageActions, inspection.Packages, "lightdm", true, warnings, "Local XFCE login manager.");
            serviceActions.AddRange(BuildSwitchToDisplayManagerActions(inspection.Services, "lightdm.service"));
        }
        if (request.ConfigureRemoteDesktop)
        {
            if (inspection.XrdpInstalled || request.InstallXrdpIfMissing) serviceActions.AddRange(BuildXrdpServiceActions(inspection));
            else warnings.Add("Skipping XRDP service enablement because XRDP is missing and installation was not selected.");
        }
        warnings.Add("The desktop changes apply to new sessions. Existing desktops and user files are retained.");
        if (request.RepairDesktop) AddDesktopRepairPackages(packageActions, "xfce4-session", "xfwm4", "xfdesktop4", "xfce4-panel", "xfce4-settings");
        if (request.BindNewXrdpToLoopback && !inspection.XrdpInstalled)
            serviceActions.Insert(0, new(ServiceActionKind.Unmask, "xrdp.service", "Start the new XRDP listener only after its private configuration is ready.", false, "systemctl unmask --runtime xrdp.service") { RuntimeOnly = true });
        var sessionChanges = await sessionConfigurationService.BuildOptimizationChangesAsync(inspection, request, cancellationToken);
        return BuildPlan(request, inspection, packageActions, serviceActions, sessionChanges, warnings);
    }

    private async Task<RdpOptimizationPlan> BuildDesktopPlanAsync(
        RdpOptimizationRequest request,
        DesktopInspectionReport inspection,
        IReadOnlyList<string> requiredPackages,
        string targetDisplayManagerService,
        string modeName,
        CancellationToken cancellationToken)
    {
        var packageActions = new List<PackageAction>();
        var serviceActions = new List<ServiceAction>();
        var warnings = new List<string>();

        var needsManager = request.ConfigureDesktop && (request.RepairDesktop || DesktopSessionCatalog.NeedsDisplayManager(request.Profile, inspection.SessionConfiguration.DisplayManager));
        var desktopPackages = request.Profile == RdpOptimizationProfile.GnomeDesktop && !request.ConfigureDesktop
            ? new[] { "gnome-session", "gnome-shell" } : requiredPackages.Where(name => needsManager || name is not ("gdm3" or "sddm"));
        foreach (var packageName in desktopPackages)
        {
            AddInstallActionIfMissing(packageActions, inspection.Packages, packageName, installIfMissing: true, warnings, $"{modeName} mode needs this package.");
        }

        if (needsManager)
            serviceActions.AddRange(BuildSwitchToDisplayManagerActions(inspection.Services, targetDisplayManagerService));
        if (request.ConfigureRemoteDesktop)
        {
            foreach (var packageName in RdpOptimizerCatalog.RequiredXrdpPackages)
                AddInstallActionIfMissing(packageActions, inspection.Packages, packageName, true, warnings, "Remote desktop requires this package.");
            serviceActions.AddRange(BuildXrdpServiceActions(inspection));
            warnings.Add("Remote sessions require an installed X11 session for the selected desktop. Wayland-only desktops cannot be launched by XRDP/Xorg.");
        }
        warnings.Add("The desktop changes apply to new sessions. Existing desktops and user files are retained.");
        if (request.RepairDesktop)
            AddDesktopRepairPackages(packageActions, request.Profile == RdpOptimizationProfile.GnomeDesktop
                ? ["gnome-shell", "gnome-session-bin", "ubuntu-session"] : ["plasma-workspace", "plasma-desktop"]);
        var sessionChanges = await sessionConfigurationService.BuildOptimizationChangesAsync(inspection, request, cancellationToken);
        return BuildPlan(request, inspection, packageActions, serviceActions, sessionChanges, warnings);
    }

    private static void AddDesktopRepairPackages(List<PackageAction> actions, params string[] packages)
    {
        foreach (var name in packages.Where(name => actions.All(action => action.PackageName != name)))
            actions.Add(new(PackageActionKind.Reinstall, name, "Restore the desktop's package files; user data and other desktops are retained.",
                false, $"apt-get install --reinstall -y {name}"));
    }

    private Task<RdpOptimizationPlan> BuildHeadlessPlanAsync(
        RdpOptimizationRequest request,
        DesktopInspectionReport inspection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var serviceActions = new List<ServiceAction>();
        foreach (var serviceName in KnownDisplayManagers)
        {
            var service = inspection.Services.FirstOrDefault(item => item.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            if (service?.IsActive == true)
            {
                serviceActions.Add(new ServiceAction(
                    ServiceActionKind.Stop,
                    service.Name,
                    "Stop the graphical or XRDP service for console-only mode.",
                    true,
                    $"systemctl stop {service.Name}"));
            }

            if (service?.IsEnabled == true)
            {
                serviceActions.Add(new ServiceAction(
                    ServiceActionKind.Disable,
                    service.Name,
                    "Disable the graphical or XRDP service for console-only mode.",
                    true,
                    $"systemctl disable {service.Name}"));
            }
        }

        var warnings = new List<string>
        {
            "Headless console mode disables local graphical login managers. Remote desktop services and installed desktops are preserved."
        };

        return Task.FromResult(BuildPlan(
            request,
            inspection,
            [],
            serviceActions,
            [],
            warnings));
    }

    private async Task<RdpOptimizationPlan> BuildRestorePlanAsync(
        RdpOptimizationRequest request,
        DesktopInspectionReport inspection,
        CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(request, cancellationToken);
        var warnings = new List<string>();

        if (snapshot is null)
        {
            warnings.Add("No restore snapshot was found. Restore mode cannot safely reconstruct previous desktop state.");
            return new RdpOptimizationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                request.Profile,
                request.DryRun,
                false,
                inspection,
                Array.Empty<PackageAction>(),
                Array.Empty<ServiceAction>(),
                Array.Empty<SessionConfigurationChange>(),
                Array.Empty<string>(),
                warnings,
                false);
        }

        var packageActions = new List<PackageAction>();
        if (request.RestoreRemovedPackages)
        {
            packageActions.AddRange(snapshot.RemovedPackages
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(packageName => new PackageAction(
                    PackageActionKind.Reinstall,
                    packageName,
                    "This package was removed by the desktop mode switch and is being restored.",
                    false,
                    $"apt-get install --reinstall -y {packageName}")));
        }

        if (request.RestoreRemovedPackages) packageActions.AddRange(BuildPackageRemovalActionsForSnapshotRestore(snapshot.RelevantPackages, inspection.Packages));

        var serviceActions = BuildRestoreServiceActions(snapshot.RelevantServices, inspection.Services);
        var sessionChanges = snapshot.FileBackups
            .Select(backup => new SessionConfigurationChange(
                backup.SourcePath,
                $"Restore {backup.SourcePath} from snapshot {snapshot.SnapshotId:N}.",
                backup.BackupPath,
                false,
                false))
            .ToArray();

        return new RdpOptimizationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.Profile,
            request.DryRun,
            false,
            inspection,
            packageActions.ToArray(),
            serviceActions,
            sessionChanges,
            Array.Empty<string>(),
            warnings,
            packageActions.Count > 0 || serviceActions.Any(action => action.IsDestructive));
    }

    private static IReadOnlyList<PackageAction> BuildPackageRemovalActionsForSnapshotRestore(
        IReadOnlyList<PackageState> snapshotPackages,
        IReadOnlyList<PackageState> currentPackages)
    {
        var snapshotByName = snapshotPackages
            .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return currentPackages
            .Where(package => package.IsInstalled &&
                              snapshotByName.TryGetValue(package.Name, out var snapshotPackage) &&
                              !snapshotPackage.IsInstalled)
            .Select(package => new PackageAction(
                PackageActionKind.Remove,
                package.Name,
                "This package was not installed when the restore snapshot was captured, so restore removes it.",
                true,
                $"apt-get remove -y {package.Name}"))
            .ToArray();
    }

    private static RdpOptimizationPlan BuildInspectOnlyPlan(
        RdpOptimizationRequest request,
        DesktopInspectionReport inspection) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.Profile,
            request.DryRun,
            true,
            inspection,
            Array.Empty<PackageAction>(),
            Array.Empty<ServiceAction>(),
            Array.Empty<SessionConfigurationChange>(),
            Array.Empty<string>(),
            BuildRecommendations(inspection),
            false);

    private async Task<RestoreSnapshot?> ResolveSnapshotAsync(
        RdpOptimizationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SnapshotIdToRestore.HasValue)
        {
            return await restoreSnapshotService.GetSnapshotAsync(request.SnapshotIdToRestore.Value, cancellationToken);
        }

        return (await restoreSnapshotService.ListSnapshotsAsync(cancellationToken)).FirstOrDefault();
    }

    private static RdpOptimizationRequest Map(RdpOptimizationRequestEditor editor) =>
        new()
        {
            Profile = editor.Profile,
            BindNewXrdpToLoopback = editor.BindNewXrdpToLoopback,
            ConfigureRemoteDesktop = !editor.RepairDesktop && editor.ConfigureRemoteDesktop,
            ConfigureDesktop = editor.ConfigureDesktop || editor.RepairDesktop,
            RepairDesktop = editor.RepairDesktop,
            LocalUserName = editor.LocalUserName,
            DesktopUserNames = editor.DesktopUserNames,
            InstallXrdpIfMissing = editor.InstallXrdpIfMissing,
            InstallXfceIfMissing = editor.InstallXfceIfMissing,
            DisableGnomeServicesOnly = editor.DisableGnomeServicesOnly,
            DisableGnomeAutostarts = editor.DisableGnomeAutostarts,
            CreateSnapshotBeforeChanges = editor.CreateSnapshotBeforeChanges,
            DryRun = editor.DryRun,
            InspectOnly = editor.InspectOnly || editor.Profile == RdpOptimizationProfile.Default,
            RestoreRemovedPackages = editor.RestoreRemovedPackages,
            SnapshotIdToRestore = editor.SnapshotIdToRestore,
            SelectedGnomePackagesToRemove = editor.SelectedGnomePackagesToRemove.OrderBy(item => item).ToArray()
        };

    private static IReadOnlyList<RdpDesktopModeOptionViewModel> BuildModeOptions(DesktopInspectionReport inspection)
    {
        var currentModeLabel = DetermineCurrentModeLabel(inspection);

        return
        [
            BuildModeOption(
                inspection,
                currentModeLabel,
                RdpOptimizationProfile.HeadlessConsole,
                "Headless / console",
                "Disable graphical logins and XRDP, leaving the box as a console-first server.",
                Array.Empty<string>(),
                [
                    "Stops graphical display managers.",
                    "Stops XRDP services.",
                    "Leaves desktop packages installed."
                ]),
            BuildModeOption(
                inspection,
                currentModeLabel,
                RdpOptimizationProfile.RdpOptimizedXfce,
                "XFCE RDP Optimized",
                "Use XFCE with lightdm and point XRDP sessions at XFCE for a leaner remote desktop path.",
                RdpOptimizerCatalog.RequiredXrdpPackages.Concat(RdpOptimizerCatalog.RequiredXfcePackages).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                [
                    "Installs XFCE, lightdm, and XRDP when missing.",
                    "Makes XRDP launch XFCE.",
                    "Disables heavier display managers."
                ]),
            BuildModeOption(
                inspection,
                currentModeLabel,
                RdpOptimizationProfile.GnomeDesktop,
                "GNOME",
                "Use the stock Ubuntu GNOME desktop with GDM as the login manager.",
                RdpOptimizerCatalog.RequiredGnomePackages,
                [
                    "Installs ubuntu-desktop-minimal when needed.",
                    "Enables GDM.",
                    "Resets XRDP back to the system default session."
                ]),
            BuildModeOption(
                inspection,
                currentModeLabel,
                RdpOptimizationProfile.KdePlasmaDesktop,
                "KDE Plasma",
                "Use KDE Plasma Desktop with SDDM as the login manager.",
                RdpOptimizerCatalog.RequiredKdePackages,
                [
                    "Installs KDE Plasma Desktop when needed.",
                    "Enables SDDM.",
                    "Resets XRDP back to the system default session."
                ])
        ];
    }

    private static RdpDesktopModeOptionViewModel BuildModeOption(
        DesktopInspectionReport inspection,
        string currentModeLabel,
        RdpOptimizationProfile profile,
        string title,
        string summary,
        IReadOnlyList<string> requiredPackages,
        IReadOnlyList<string> highlights)
    {
        var missingPackages = requiredPackages
            .Where(packageName => !IsInstalled(inspection.Packages, packageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RdpDesktopModeOptionViewModel(
            profile,
            title,
            summary,
            string.Equals(currentModeLabel, title, StringComparison.OrdinalIgnoreCase),
            missingPackages.Length == 0,
            missingPackages,
            highlights);
    }

    private static RdpOptimizationPlan BuildPlan(
        RdpOptimizationRequest request,
        DesktopInspectionReport inspection,
        IReadOnlyList<PackageAction> packageActions,
        IReadOnlyList<ServiceAction> serviceActions,
        IReadOnlyList<SessionConfigurationChange> sessionChanges,
        IReadOnlyCollection<string> warnings)
    {
        var warningList = warnings
            .Concat(inspection.LikelyIssues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RdpOptimizationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.Profile,
            request.DryRun,
            false,
            inspection,
            packageActions,
            serviceActions,
            sessionChanges,
            sessionChanges
                .Where(change => change.FilePath.Contains("/etc/xdg/autostart/", StringComparison.OrdinalIgnoreCase))
                .Select(change => change.Description)
                .ToArray(),
            warningList,
            packageActions.Any(item => item.IsDestructive) ||
            serviceActions.Any(item => item.IsDestructive) ||
            sessionChanges.Any(item => item.IsDestructive));
    }

    private static void AddInstallActionIfMissing(
        ICollection<PackageAction> packageActions,
        IReadOnlyList<PackageState> packages,
        string packageName,
        bool installIfMissing,
        ICollection<string> warnings,
        string reason)
    {
        var isInstalled = packages.Any(item => item.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase) && item.IsInstalled);
        if (isInstalled)
        {
            return;
        }

        if (installIfMissing)
        {
            packageActions.Add(new PackageAction(
                PackageActionKind.Install,
                packageName,
                reason,
                false,
                $"apt-get install -y --no-install-recommends {packageName}") { NoInstallRecommends = true });
        }
        else
        {
            warnings.Add($"{packageName} is missing, but installation was not selected.");
        }
    }

    private static IReadOnlyList<ServiceAction> BuildXrdpServiceActions(DesktopInspectionReport inspection)
    {
        var actions = new List<ServiceAction>();
        foreach (var serviceName in new[] { "xrdp.service", "xrdp-sesman.service" })
        {
            var service = inspection.Services.FirstOrDefault(item => item.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            var resolvedName = service?.Name ?? serviceName;
            if (service is null || !service.IsEnabled)
            {
                actions.Add(new ServiceAction(
                    ServiceActionKind.Enable,
                    resolvedName,
                    "XRDP needs to be enabled on boot.",
                    false,
                    $"systemctl enable {resolvedName}"));
            }

            if (service is null || !service.IsActive)
            {
                actions.Add(new ServiceAction(
                    ServiceActionKind.Start,
                    resolvedName,
                    "XRDP needs to be running for remote sessions.",
                    false,
                    $"systemctl start {resolvedName}"));
            }
        }

        return actions;
    }

    private static IReadOnlyList<ServiceAction> BuildSwitchToDisplayManagerActions(
        IReadOnlyList<ServiceState> services, string targetServiceName)
    {
        var actions = services.Where(s => KnownDisplayManagers.Contains(s.Name, StringComparer.OrdinalIgnoreCase) &&
                !s.Name.Equals(targetServiceName, StringComparison.OrdinalIgnoreCase) && s.IsEnabled)
            .Select(s => new ServiceAction(ServiceActionKind.Disable, s.Name,
                "Change the next local login manager without ending the current desktop.", false, $"systemctl disable {s.Name}")).ToList();
        actions.Add(new ServiceAction(ServiceActionKind.Enable, targetServiceName,
            "Select the login manager for the next console start. Running sessions remain open.", false,
            $"systemctl enable --force {targetServiceName}") { Force = true });
        return actions;
    }

    private static IReadOnlyList<ServiceAction> BuildRestoreServiceActions(
        IReadOnlyList<ServiceState> snapshotServices,
        IReadOnlyList<ServiceState> currentServices)
    {
        var actions = new List<ServiceAction>();

        foreach (var snapshotService in snapshotServices)
        {
            var current = currentServices.FirstOrDefault(item => item.Name.Equals(snapshotService.Name, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                continue;
            }

            if (!snapshotService.IsMasked && current.IsMasked)
            {
                actions.Add(new ServiceAction(ServiceActionKind.Unmask, current.Name, "Restore original unmasked service state.", false, $"systemctl unmask {current.Name}"));
            }

            if (!snapshotService.IsActive && current.IsActive)
            {
                actions.Add(new ServiceAction(ServiceActionKind.Stop, current.Name, "Restore stopped service state.", true, $"systemctl stop {current.Name}"));
            }

            if (snapshotService.IsEnabled && !current.IsEnabled)
            {
                actions.Add(new ServiceAction(ServiceActionKind.Enable, current.Name, "Restore service enablement state.", false, $"systemctl enable {current.Name}"));
            }
            else if (!snapshotService.IsEnabled && current.IsEnabled)
            {
                actions.Add(new ServiceAction(ServiceActionKind.Disable, current.Name, "Restore service disablement state.", true, $"systemctl disable {current.Name}"));
            }

            if (snapshotService.IsActive && !current.IsActive)
            {
                actions.Add(new ServiceAction(ServiceActionKind.Start, current.Name, "Restore running service state.", false, $"systemctl start {current.Name}"));
            }

            if (snapshotService.IsMasked && !current.IsMasked)
            {
                actions.Add(new ServiceAction(ServiceActionKind.Mask, current.Name, "Restore original masked service state.", true, $"systemctl mask {current.Name}"));
            }
        }

        return actions;
    }

    private static string DetermineCurrentModeLabel(DesktopInspectionReport inspection)
    {
        if (IsHeadless(inspection))
        {
            return "Headless / console";
        }

        if (inspection.XrdpInstalled && inspection.SessionConfiguration.XrdpUsesXfce && HasDesktop(inspection, "XFCE"))
        {
            return "XFCE RDP Optimized";
        }

        if (HasDesktop(inspection, "KDE Plasma"))
        {
            return "KDE Plasma";
        }

        if (HasDesktop(inspection, "GNOME"))
        {
            return "GNOME";
        }

        if (HasDesktop(inspection, "XFCE"))
        {
            return "XFCE";
        }

        return "Mixed / manual";
    }

    private static string BuildCurrentModeSummary(DesktopInspectionReport inspection, string currentModeLabel)
    {
        var desktops = inspection.InstalledDesktopEnvironments.Count == 0 ? "Headless / console" : string.Join(", ", inspection.InstalledDesktopEnvironments);
        var displayManager = inspection.DisplayManager ?? "none";
        return $"{currentModeLabel}. Installed desktops: {desktops}. Display manager: {displayManager}.";
    }

    private static RdpOptimizationProfile DetermineSuggestedProfile(DesktopInspectionReport inspection)
    {
        if (inspection.XrdpInstalled && inspection.SessionConfiguration.XrdpUsesXfce && HasDesktop(inspection, "XFCE"))
        {
            return RdpOptimizationProfile.RdpOptimizedXfce;
        }

        if (HasDesktop(inspection, "KDE Plasma"))
        {
            return RdpOptimizationProfile.KdePlasmaDesktop;
        }

        if (HasDesktop(inspection, "GNOME"))
        {
            return RdpOptimizationProfile.GnomeDesktop;
        }

        return RdpOptimizationProfile.RdpOptimizedXfce;
    }

    private static IReadOnlyList<string> BuildRecommendations(DesktopInspectionReport inspection)
    {
        var recommendations = new List<string>();

        if (!inspection.XrdpInstalled)
        {
            recommendations.Add("XRDP is not installed yet. Select XFCE RDP Optimized if you want a lighter remote desktop path.");
        }

        if (inspection.InstalledDesktopEnvironments.Count > 1)
        {
            recommendations.Add("Multiple desktop stacks are installed. Pick one managed mode so login managers and session defaults stop drifting.");
        }

        if (inspection.SessionConfiguration.XrdpUsesXfce && !HasDesktop(inspection, "XFCE"))
        {
            recommendations.Add("XRDP is pinned to XFCE, but XFCE packages do not look complete. Re-apply XFCE RDP Optimized to repair the session path.");
        }

        if (IsHeadless(inspection))
        {
            recommendations.Add("The machine is already close to console-only mode. Re-enable a desktop mode only when you actually need a graphical login path.");
        }
        else if (!inspection.SessionConfiguration.XrdpUsesXfce && HasDesktop(inspection, "GNOME"))
        {
            recommendations.Add("GNOME is active. If this box is mainly used over RDP, XFCE RDP Optimized will usually feel lighter.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("The current desktop stack is already in a managed state. Preview a plan before switching modes.");
        }

        return recommendations;
    }

    private static IReadOnlyList<string> CollectFilesToBackup(RdpOptimizationPlan plan) =>
        plan.SessionChanges
            .Where(change => change.RequiresBackup)
            .Select(change => change.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> CollectPlannedCommands(RdpOptimizationPlan plan) =>
        plan.PackageActions.Select(action => action.PlannedCommand)
            .Concat(plan.ServiceActions.Select(action => action.PlannedCommand))
            .Concat(plan.SessionChanges.Select(change => $"write {change.FilePath}"))
            .ToArray();

    private static bool HasDesktop(DesktopInspectionReport inspection, string desktopName) =>
        inspection.InstalledDesktopEnvironments.Contains(desktopName, StringComparer.OrdinalIgnoreCase);

    private static bool IsInstalled(IReadOnlyList<PackageState> packages, string packageName) =>
        packages.Any(item => item.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase) && item.IsInstalled);

    private static bool IsHeadless(DesktopInspectionReport inspection) =>
        !inspection.Services.Any(item =>
            KnownDisplayManagers.Contains(item.Name, StringComparer.OrdinalIgnoreCase) &&
            (item.IsActive || item.IsEnabled)) &&
        !inspection.XrdpServiceActive &&
        !inspection.XrdpServiceEnabled;

    private static bool AnyEnabledDisplayManager(IReadOnlyList<ServiceState> services, params string[] serviceNames) =>
        services.Any(item =>
            serviceNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase) &&
            (item.IsActive || item.IsEnabled));

    private static string ResolveDisplayManagerServiceName(
        IReadOnlyList<ServiceState> services,
        string preferredServiceName,
        params string[] aliases)
    {
        var candidates = new[] { preferredServiceName }.Concat(aliases);
        return services
                   .Select(service => service.Name)
                   .FirstOrDefault(name => candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
               ?? preferredServiceName;
    }
}
