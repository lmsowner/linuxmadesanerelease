using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Services;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteLinuxServiceModuleDataService(LinuxMadeSaneDbContext dbContext) : ILinuxServiceModuleDataService
{
    public async Task<IReadOnlyList<LinuxServiceDefinition>> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var liveServices = await LoadLiveServicesAsync(cancellationToken);
        var overrides = await dbContext.LinuxServices
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var overridesByUnit = overrides.ToDictionary(item => item.UnitName, StringComparer.OrdinalIgnoreCase);

        return liveServices
            .Select(service => overridesByUnit.TryGetValue(service.UnitName, out var entity) ? Merge(service, entity) : service)
            .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<LinuxServiceDefinition?> GetServiceAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var services = await ListServicesAsync(cancellationToken);
        var selected = services.FirstOrDefault(service => service.Id == serviceId);
        if (selected is not null)
        {
            var details = await GetLiveServiceDetailsAsync(selected.UnitName, cancellationToken);
            return details is null ? selected : Merge(details, await FindOverrideAsync(selected.UnitName, cancellationToken));
        }

        var entity = await dbContext.LinuxServices
            .AsNoTracking()
            .SingleOrDefaultAsync(service => service.Id == serviceId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveServiceAsync(LinuxServiceDefinition service, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LinuxServices
            .SingleOrDefaultAsync(item => item.Id == service.Id || item.UnitName == service.UnitName, cancellationToken);

        if (entity is null)
        {
            dbContext.LinuxServices.Add(Map(service));
        }
        else
        {
            entity.UnitName = service.UnitName;
            entity.DisplayName = service.DisplayName;
            entity.HostName = service.HostName;
            entity.Summary = service.Summary;
            entity.RuntimeState = (int)service.RuntimeState;
            entity.HealthStatus = (int)service.HealthStatus;
            entity.EnabledAtBoot = service.EnabledAtBoot;
            entity.ActiveUnderSystemd = service.ActiveUnderSystemd;
            entity.RunningUser = service.RunningUser;
            entity.RunningGroup = service.RunningGroup;
            entity.WorkingDirectory = service.WorkingDirectory;
            entity.ExecStart = service.ExecStart;
            entity.EnvironmentFile = service.EnvironmentFile;
            entity.RestartCount = service.RestartCount;
            entity.LastStartTime = service.LastStartTime;
            entity.ListeningPort = service.ListeningPort;
            entity.RecentErrorsJson = Serialize(service.RecentErrors);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteServiceAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LinuxServices.SingleOrDefaultAsync(service => service.Id == serviceId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.LinuxServices.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ServiceControlResult> ControlServiceAsync(
        Guid serviceId,
        ServiceControlAction action,
        CancellationToken cancellationToken = default)
    {
        var service = await GetServiceAsync(serviceId, cancellationToken)
            ?? throw new InvalidOperationException("Service was not found on the local machine.");

        var verb = action switch
        {
            ServiceControlAction.Start => "start",
            ServiceControlAction.Stop => "stop",
            _ => "restart"
        };

        var result = await RunCommandResultAsync(
            "systemctl",
            [verb, service.UnitName],
            cancellationToken,
            allowFailure: true);

        var success = result.ExitCode == 0;
        var message = success
            ? $"{action} sent to {service.UnitName}."
            : string.IsNullOrWhiteSpace(result.StandardError)
                ? $"systemctl {verb} {service.UnitName} failed with exit code {result.ExitCode}."
                : result.StandardError.Trim();

        return new ServiceControlResult(service.Id, service.UnitName, action, success, message);
    }

    public async Task<ServiceInspectionResult> InspectServiceAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await GetServiceAsync(serviceId, cancellationToken)
            ?? throw new InvalidOperationException("Service was not found on the local machine.");

        var properties = await GetSystemdPropertiesAsync(service.UnitName, cancellationToken)
            ?? throw new InvalidOperationException("systemd did not return details for this service.");

        var liveLogLines = await GetJournalLinesAsync(service.UnitName, cancellationToken);
        var environmentFile = NormalizeEnvironmentFile(properties);
        var restartPolicy = GetProperty(properties, "Restart", "no");
        var unitFileState = GetProperty(properties, "UnitFileState", service.EnabledAtBoot ? "enabled" : "disabled");
        var lastExitCode = ParseInt(GetProperty(properties, "ExecMainStatus"));
        var restartCount = ParseInt(GetProperty(properties, "NRestarts"));
        var activeState = GetProperty(properties, "ActiveState", string.Empty);
        var runningUser = NormalizeServiceIdentity(GetProperty(properties, "User"), GetProperty(properties, "UID"), "root");
        var runningGroup = NormalizeServiceIdentity(GetProperty(properties, "Group"), GetProperty(properties, "GID"), "root");
        var workingDirectory = GetProperty(properties, "WorkingDirectory", service.WorkingDirectory);
        var execStart = NormalizeExecStart(GetProperty(properties, "ExecStart"), service.ExecStart);
        var dependencies = ParseDependencies(GetProperty(properties, "After"));

        var daemonReloadRequired = execStart.Contains("/current/", StringComparison.OrdinalIgnoreCase) && restartCount > 0;
        var restartLoop = activeState.Equals("failed", StringComparison.OrdinalIgnoreCase) || restartCount >= 5;
        var configDrift = liveLogLines.Any(line =>
            line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not found", StringComparison.OrdinalIgnoreCase));
        var healthStatus = DetermineHealth(activeState, restartLoop, configDrift);
        var portAvailable = service.ListeningPort == 0 || !restartLoop;

        return new ServiceInspectionResult(
            service.Id,
            service.UnitName,
            GetProperty(properties, "FragmentPath", $"/etc/systemd/system/{service.UnitName}"),
            execStart,
            workingDirectory,
            environmentFile,
            runningUser,
            runningGroup,
            restartPolicy,
            dependencies,
            liveLogLines.Count > 0 ? liveLogLines : ["No recent journal lines were returned for this service."],
            lastExitCode,
            healthStatus,
            unitFileState,
            BuildVersionHint(service.LastStartTime),
            daemonReloadRequired,
            !string.Equals(runningUser, "root", StringComparison.OrdinalIgnoreCase),
            !string.IsNullOrWhiteSpace(workingDirectory),
            !string.IsNullOrWhiteSpace(environmentFile),
            portAvailable,
            !execStart.Contains("previous", StringComparison.OrdinalIgnoreCase),
            !execStart.Contains("old", StringComparison.OrdinalIgnoreCase),
            false,
            restartLoop,
            configDrift,
            BuildSummary(service with
            {
                RunningUser = runningUser,
                RunningGroup = runningGroup,
                WorkingDirectory = workingDirectory,
                ExecStart = execStart,
                EnvironmentFile = environmentFile,
                HealthStatus = healthStatus
            }, !string.IsNullOrWhiteSpace(environmentFile), daemonReloadRequired, healthStatus == ServiceHealthStatus.Healthy),
            BuildFindings(service with
            {
                RuntimeState = MapRuntimeState(activeState, GetProperty(properties, "SubState")),
                RunningUser = runningUser,
                RunningGroup = runningGroup,
                WorkingDirectory = workingDirectory,
                ExecStart = execStart,
                EnvironmentFile = environmentFile,
                RestartCount = restartCount,
                RecentErrors = liveLogLines.Where(line => line.Contains("fail", StringComparison.OrdinalIgnoreCase)).ToArray()
            }, !string.IsNullOrWhiteSpace(environmentFile), daemonReloadRequired, portAvailable, configDrift));
    }

    public async Task<ServiceUpdateIssueReport> GetUpdateIssueReportAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await GetServiceAsync(serviceId, cancellationToken)
            ?? throw new InvalidOperationException("Service was not found on the local machine.");

        var issues = new List<ServiceRepairIssue>();

        if (service.RuntimeState == ServiceRuntimeState.Failed)
        {
            issues.Add(new ServiceRepairIssue(
                RepairRiskLevel.High,
                ServiceDiagnosticSeverity.Critical,
                "Service is already failed",
                "The live systemd unit is already in a failed state.",
                "Deploying on top of a broken baseline makes the next failure much harder to reason about.",
                "Inspect live status and logs first."));
        }

        if (string.IsNullOrWhiteSpace(service.EnvironmentFile))
        {
            issues.Add(new ServiceRepairIssue(
                RepairRiskLevel.Medium,
                ServiceDiagnosticSeverity.Warning,
                "No environment file configured",
                "The service is not using a dedicated environment file.",
                "That usually pushes runtime config into the unit or shell wrappers.",
                "Move volatile settings into a clear environment file."));
        }

        if (service.RunningUser.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ServiceRepairIssue(
                RepairRiskLevel.High,
                ServiceDiagnosticSeverity.Warning,
                "Service runs as root",
                "The live unit appears to run under root defaults.",
                "That widens blast radius and often hides ownership issues.",
                "Move it to a dedicated service account if this is an app workload."));
        }

        if (issues.Count == 0)
        {
            issues.Add(new ServiceRepairIssue(
                RepairRiskLevel.Low,
                ServiceDiagnosticSeverity.Informational,
                "No obvious update blockers",
                "The live service looks stable enough for a controlled update.",
                "That still does not replace validating the release payload and config changes.",
                "Run through the update checks in order."));
        }

        var oneClick = new List<string>();
        if (service.RuntimeState == ServiceRuntimeState.Failed)
        {
            oneClick.Add("Capture live status and recent logs before changing files.");
        }

        if (string.IsNullOrWhiteSpace(service.EnvironmentFile))
        {
            oneClick.Add("Define an environment file path before the next deployment.");
        }

        return new ServiceUpdateIssueReport(service.Id, service.UnitName, issues, oneClick);
    }

    public async Task<ServiceUpdatePlan> GetUpdatePlanAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await GetServiceAsync(serviceId, cancellationToken)
            ?? throw new InvalidOperationException("Service was not found on the local machine.");

        var requiresConfigMerge = service.RecentErrors.Any(error => error.Contains("drift", StringComparison.OrdinalIgnoreCase));
        var requiresOwnershipCorrection = service.RuntimeState == ServiceRuntimeState.Failed || service.RunningUser == "root";
        var requiresDaemonReload = service.ExecStart.Contains("/current/", StringComparison.OrdinalIgnoreCase);

        return new ServiceUpdatePlan(
            service.Id,
            service.UnitName,
            BuildVersionHint(service.LastStartTime),
            service.RuntimeState == ServiceRuntimeState.Failed ? RepairRiskLevel.High : RepairRiskLevel.Medium,
            requiresDaemonReload,
            requiresOwnershipCorrection,
            !string.IsNullOrWhiteSpace(service.EnvironmentFile),
            requiresConfigMerge,
            ["Folder", "Archive", "Package"],
            BuildValidationChecks(service),
            BuildPlannedSteps(service, requiresDaemonReload, requiresOwnershipCorrection),
            ["Service starts cleanly under systemd", "Expected port binds successfully", "Health endpoint or startup verification passes"],
            ["Unit fails to start", "Port remains blocked", "New release validation fails"],
            ["Skipping daemon-reload can leave systemd on stale metadata.", "Restarting before ownership correction can fail for the wrong reason."]);
    }

    public async Task<ServiceRepairPlan> GetRepairPlanAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await GetServiceAsync(serviceId, cancellationToken)
            ?? throw new InvalidOperationException("Service was not found on the local machine.");

        var issues = new List<ServiceRepairIssue>();

        if (service.RuntimeState == ServiceRuntimeState.Failed)
        {
            issues.Add(new ServiceRepairIssue(RepairRiskLevel.High, ServiceDiagnosticSeverity.Critical, "Service is failed", "The live unit is currently failed.", "Operators will chase deployment details while the baseline is already broken.", "Inspect live status and logs before touching release files."));
        }

        if (service.RunningUser == "root")
        {
            issues.Add(new ServiceRepairIssue(RepairRiskLevel.High, ServiceDiagnosticSeverity.Critical, "Runs as root", "The unit is relying on root defaults.", "That increases blast radius and often hides ownership mistakes until later.", "Move the unit to a dedicated service account where appropriate."));
        }

        if (string.IsNullOrWhiteSpace(service.EnvironmentFile))
        {
            issues.Add(new ServiceRepairIssue(RepairRiskLevel.Medium, ServiceDiagnosticSeverity.Warning, "No environment file", "Runtime settings are not separated from the unit or binary path.", "Operational changes will be brittle and harder to diff.", "Introduce an environment file and keep it under change control."));
        }

        if (service.ListeningPort > 0 && service.RuntimeState == ServiceRuntimeState.Failed)
        {
            issues.Add(new ServiceRepairIssue(RepairRiskLevel.Medium, ServiceDiagnosticSeverity.Warning, "Port may be blocked", $"The service expects port {service.ListeningPort}, but the unit is failed.", "A stale process may still be holding the port.", "Verify the active PID before restarting through systemd."));
        }

        if (issues.Count == 0)
        {
            issues.Add(new ServiceRepairIssue(RepairRiskLevel.Low, ServiceDiagnosticSeverity.Informational, "No high-risk repair flags", "The live service looks internally sane.", "You still need to confirm the deployment files and runtime dependencies.", "Run the repair sequence and compare it to live status."));
        }

        return new ServiceRepairPlan(
            service.Id,
            service.UnitName,
            issues,
            ["Capture `systemctl status` and recent logs.", "Verify user, group, working directory, and env file.", "Normalize ownership and runtime paths.", "Reload systemd if the unit definition changed.", "Restart and verify the expected port and health checks."]);
    }

    public Task<IReadOnlyList<ServiceDeploymentPattern>> ListDeploymentPatternsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ServiceDeploymentPattern> patterns =
        [
            new(DeploymentPatternType.AspNetKestrel, "ASP.NET / Kestrel app", "Create a systemd-backed ASP.NET deployment from a release folder with sane user, working directory, and health defaults.", "You get a versioned release path, a dedicated service account, a health-checked systemd unit, and a predictable logs/data layout.", ["Dedicated app user and group", "Versioned release folders with current symlink", "Environment file wired into systemd", "Health endpoint check after restart"], ["systemd unit", "environment file", "release folder layout", "logs and data directories"]),
            new(DeploymentPatternType.NodeService, "Node service", "Set up a long-running Node process with working directory, environment file, and restart policy already sane.", "The service starts under the right user, from the right directory, with npm/runtime drift called out early.", ["Explicit node exec path", "Writable logs/data paths", "Restart on failure", "Environment file support"], ["systemd unit", "logs directory", "runtime checklist"]),
            new(DeploymentPatternType.PythonApp, "Python app", "Create a Python service with venv-aware paths and dependency validation.", "The service definition stays readable and the runtime assumptions are explicit.", ["Dedicated venv path", "Stable working directory", "Environment file for secrets/config", "Dependency check before restart"], ["systemd unit", "venv path conventions", "health checklist"]),
            new(DeploymentPatternType.DockerBackedService, "Docker-backed service", "Wrap a containerized workload in a predictable host-side control pattern.", "Container services still need sane host paths, env handling, and restart expectations.", ["Explicit compose or run command", "Environment file separation", "Health check and restart verification", "Host bind paths documented"], ["systemd wrapper unit", "env file", "host path checklist"])
        ];

        return Task.FromResult(patterns);
    }

    private async Task<IReadOnlyList<LinuxServiceDefinition>> LoadLiveServicesAsync(CancellationToken cancellationToken)
    {
        var serviceRows = await RunCommandAsync(
            "systemctl",
            ["list-units", "--type=service", "--all", "--no-legend", "--plain"],
            cancellationToken);

        var unitFileRows = await RunCommandAsync(
            "systemctl",
            ["list-unit-files", "--type=service", "--no-legend", "--plain"],
            cancellationToken);

        var unitStates = ParseUnitFileStates(unitFileRows);

        return serviceRows
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseListUnitRow)
            .Where(row => row is not null)
            .Select(row => Map(row!, unitStates))
            .ToArray();
    }

    private async Task<LinuxServiceDefinition?> GetLiveServiceDetailsAsync(string unitName, CancellationToken cancellationToken)
    {
        var properties = await GetSystemdPropertiesAsync(unitName, cancellationToken);
        if (properties is null)
        {
            return null;
        }

        var logs = await GetJournalLinesAsync(unitName, cancellationToken);
        var activeState = GetProperty(properties, "ActiveState", string.Empty);
        var subState = GetProperty(properties, "SubState", string.Empty);
        var description = GetProperty(properties, "Description", unitName);
        var environmentFile = NormalizeEnvironmentFile(properties);
        var runningUser = NormalizeServiceIdentity(GetProperty(properties, "User"), GetProperty(properties, "UID"), "root");
        var runningGroup = NormalizeServiceIdentity(GetProperty(properties, "Group"), GetProperty(properties, "GID"), "root");
        var lastStart = ParseTimestamp(GetProperty(properties, "ExecMainStartTimestamp"));
        var restartCount = ParseInt(GetProperty(properties, "NRestarts"));

        return new LinuxServiceDefinition(
            CreateStableId(unitName),
            unitName,
            description,
            Environment.MachineName,
            BuildSummaryText(activeState, subState, description),
            MapRuntimeState(activeState, subState),
            DetermineHealth(activeState, restartCount >= 5, logs.Any(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase))),
            GetProperty(properties, "UnitFileState", string.Empty).Contains("enabled", StringComparison.OrdinalIgnoreCase),
            GetProperty(properties, "LoadState", "loaded").Equals("loaded", StringComparison.OrdinalIgnoreCase),
            runningUser,
            runningGroup,
            GetProperty(properties, "WorkingDirectory", string.Empty),
            NormalizeExecStart(GetProperty(properties, "ExecStart"), string.Empty),
            environmentFile,
            restartCount,
            lastStart,
            0,
            logs.Where(line => line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5).ToArray());
    }

    private async Task<Dictionary<string, string>?> GetSystemdPropertiesAsync(string unitName, CancellationToken cancellationToken)
    {
        var output = await RunCommandAsync(
            "systemctl",
            [
                "show",
                unitName,
                "--property=Id,Description,LoadState,ActiveState,SubState,UnitFileState,User,Group,WorkingDirectory,ExecStart,EnvironmentFiles,NRestarts,ExecMainStartTimestamp,FragmentPath,After,Result,UID,GID,Restart,ExecMainStatus",
                "--no-pager"
            ],
            cancellationToken,
            allowFailure: true);

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> GetJournalLinesAsync(string unitName, CancellationToken cancellationToken)
    {
        var output = await RunCommandAsync(
            "journalctl",
            ["-u", unitName, "-n", "20", "--no-pager"],
            cancellationToken,
            allowFailure: true);

        return string.IsNullOrWhiteSpace(output)
            ? Array.Empty<string>()
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<LinuxServiceEntity?> FindOverrideAsync(string unitName, CancellationToken cancellationToken) =>
        await dbContext.LinuxServices
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UnitName == unitName, cancellationToken);

    private static LinuxServiceDefinition Merge(LinuxServiceDefinition service, LinuxServiceEntity? entity)
    {
        if (entity is null)
        {
            return service;
        }

        return service with
        {
            DisplayName = string.IsNullOrWhiteSpace(entity.DisplayName) ? service.DisplayName : entity.DisplayName,
            Summary = string.IsNullOrWhiteSpace(entity.Summary) ? service.Summary : entity.Summary,
            EnvironmentFile = string.IsNullOrWhiteSpace(service.EnvironmentFile) ? NormalizeText(entity.EnvironmentFile) : service.EnvironmentFile,
            ListeningPort = entity.ListeningPort > 0 ? entity.ListeningPort : service.ListeningPort
        };
    }

    private static LinuxServiceDefinition Map(ServiceListRow row, IReadOnlyDictionary<string, string> unitStates) =>
        new(
            CreateStableId(row.UnitName),
            NormalizeText(row.UnitName),
            BuildDisplayName(row.Description, row.UnitName),
            Environment.MachineName,
            BuildSummaryText(row.ActiveState, row.SubState, row.Description),
            MapRuntimeState(row.ActiveState, row.SubState),
            DetermineHealth(row.ActiveState, false, false),
            unitStates.TryGetValue(row.UnitName, out var unitState) && unitState.Contains("enabled", StringComparison.OrdinalIgnoreCase),
            row.LoadState.Equals("loaded", StringComparison.OrdinalIgnoreCase),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            0,
            DateTimeOffset.MinValue,
            0,
            Array.Empty<string>());

    private static ServiceListRow? ParseListUnitRow(string line)
    {
        var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length < 5 ? null : new ServiceListRow(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    private static Dictionary<string, string> ParseUnitFileStates(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].EndsWith(".service", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

    private static Guid CreateStableId(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return new Guid(hash);
    }

    private static string BuildSummaryText(string activeState, string subState, string description) =>
        $"{description}. systemd reports {activeState}/{subState}.";

    private static string BuildVersionHint(DateTimeOffset lastStartTime) =>
        lastStartTime == DateTimeOffset.MinValue
            ? "live-unknown"
            : $"live-{lastStartTime:yyyy.MM.dd-HHmm}";

    private static ServiceRuntimeState MapRuntimeState(string activeState, string subState) =>
        activeState.ToLowerInvariant() switch
        {
            "active" when subState.Equals("running", StringComparison.OrdinalIgnoreCase) => ServiceRuntimeState.Running,
            "failed" => ServiceRuntimeState.Failed,
            _ => ServiceRuntimeState.Stopped
        };

    private static ServiceHealthStatus DetermineHealth(string activeState, bool restartLoop, bool configDrift)
    {
        if (activeState.Equals("failed", StringComparison.OrdinalIgnoreCase) || restartLoop)
        {
            return ServiceHealthStatus.Broken;
        }

        if (configDrift || !activeState.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceHealthStatus.Warning;
        }

        return ServiceHealthStatus.Healthy;
    }

    private static string NormalizeExecStart(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var pathMarker = "path=";
        var markerIndex = value.IndexOf(pathMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return value;
        }

        var start = markerIndex + pathMarker.Length;
        var end = value.IndexOf(" ;", start, StringComparison.Ordinal);
        return end > start ? value[start..end].Trim() : value[start..].Trim();
    }

    private static string? NormalizeEnvironmentFile(IReadOnlyDictionary<string, string> properties)
    {
        var raw = GetProperty(properties, "EnvironmentFiles");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return value.Equals("[not set]", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static IReadOnlyList<string> ParseDependencies(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6).ToArray();

    private static string NormalizeServiceIdentity(string? explicitValue, string? numericValue, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue) && !explicitValue.Equals("[not set]", StringComparison.OrdinalIgnoreCase))
        {
            return explicitValue;
        }

        if (!string.IsNullOrWhiteSpace(numericValue) && !numericValue.Equals("[not set]", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue;
        }

        return fallback;
    }

    private static string GetProperty(IReadOnlyDictionary<string, string> properties, string key, string fallback = "") =>
        properties.TryGetValue(key, out var value) ? value : fallback;

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static IReadOnlyList<string> BuildSummary(LinuxServiceDefinition service, bool environmentLoaded, bool daemonReloadRequired, bool healthyPath)
    {
        var summary = new List<string>
        {
            $"This service runs as {service.RunningUser}.",
            string.IsNullOrWhiteSpace(service.WorkingDirectory)
                ? "systemd does not declare a working directory for this unit."
                : $"It starts from {service.WorkingDirectory}.",
            service.EnabledAtBoot ? "It is enabled at boot." : "It is currently disabled at boot."
        };

        summary.Add(environmentLoaded
            ? $"It expects runtime settings from {service.EnvironmentFile}."
            : "It does not have a separate environment file configured.");

        if (daemonReloadRequired)
        {
            summary.Add("Its ExecStart suggests a symlinked release path, so daemon-reload discipline matters.");
        }

        summary.Add(healthyPath
            ? "The live service state looks healthy enough to manage confidently."
            : "The live service already contains warning signs that should be handled before the next deployment.");

        return summary;
    }

    private static IReadOnlyList<string> BuildFindings(LinuxServiceDefinition service, bool environmentLoaded, bool daemonReloadRequired, bool portAvailable, bool configDrift)
    {
        var findings = new List<string>();

        if (!environmentLoaded)
        {
            findings.Add("No environment file is configured.");
        }

        if (daemonReloadRequired)
        {
            findings.Add("Symlink-style deployment path means daemon-reload hygiene matters.");
        }

        if (!portAvailable)
        {
            findings.Add($"Port {service.ListeningPort} may still be occupied after failure.");
        }

        if (configDrift)
        {
            findings.Add("Recent journal output suggests configuration or runtime drift.");
        }

        if (service.RuntimeState == ServiceRuntimeState.Failed)
        {
            findings.Add("Service is currently failed.");
        }

        if (findings.Count == 0)
        {
            findings.Add("No major findings were generated from the live service state.");
        }

        return findings;
    }

    private static IReadOnlyList<string> BuildValidationChecks(LinuxServiceDefinition service)
    {
        var checks = new List<string>
        {
            "Validate the release source contents before touching the live deployment.",
            $"Confirm ExecStart still points at the intended binary: {service.ExecStart}.",
            string.IsNullOrWhiteSpace(service.WorkingDirectory)
                ? "Confirm the unit can start without a working directory override."
                : $"Confirm working directory exists: {service.WorkingDirectory}."
        };

        if (!string.IsNullOrWhiteSpace(service.EnvironmentFile))
        {
            checks.Add($"Confirm the environment file exists and is readable: {service.EnvironmentFile}.");
        }

        if (service.ListeningPort > 0)
        {
            checks.Add($"Confirm port {service.ListeningPort} is available for the restarted process.");
        }

        return checks;
    }

    private static IReadOnlyList<string> BuildPlannedSteps(LinuxServiceDefinition service, bool requiresDaemonReload, bool requiresOwnershipCorrection)
    {
        var steps = new List<string>
        {
            "Stage and validate the new release.",
            "Stop the unit cleanly and verify no stale process remains.",
            "Back up the current release and runtime metadata.",
            "Copy or switch to the new release payload."
        };

        if (requiresOwnershipCorrection)
        {
            steps.Add($"Normalize ownership for {service.RunningUser}:{service.RunningGroup} before restart.");
        }

        if (requiresDaemonReload)
        {
            steps.Add("Run daemon-reload before restarting the service.");
        }

        steps.Add("Restart the unit and verify health checks.");
        return steps;
    }

    private static LinuxServiceDefinition Map(LinuxServiceEntity entity) =>
        new(
            entity.Id,
            NormalizeText(entity.UnitName),
            BuildDisplayName(entity.DisplayName, entity.UnitName),
            NormalizeText(entity.HostName),
            BuildSummaryText(entity.Summary, entity.UnitName),
            (ServiceRuntimeState)entity.RuntimeState,
            (ServiceHealthStatus)entity.HealthStatus,
            entity.EnabledAtBoot,
            entity.ActiveUnderSystemd,
            NormalizeText(entity.RunningUser),
            NormalizeText(entity.RunningGroup),
            NormalizeText(entity.WorkingDirectory),
            NormalizeText(entity.ExecStart),
            NormalizeText(entity.EnvironmentFile),
            entity.RestartCount,
            entity.LastStartTime,
            entity.ListeningPort,
            Deserialize(entity.RecentErrorsJson));

    private static LinuxServiceEntity Map(LinuxServiceDefinition service) =>
        new()
        {
            Id = service.Id,
            UnitName = service.UnitName,
            DisplayName = service.DisplayName,
            HostName = service.HostName,
            Summary = service.Summary,
            RuntimeState = (int)service.RuntimeState,
            HealthStatus = (int)service.HealthStatus,
            EnabledAtBoot = service.EnabledAtBoot,
            ActiveUnderSystemd = service.ActiveUnderSystemd,
            RunningUser = service.RunningUser,
            RunningGroup = service.RunningGroup,
            WorkingDirectory = service.WorkingDirectory,
            ExecStart = service.ExecStart,
            EnvironmentFile = service.EnvironmentFile,
            RestartCount = service.RestartCount,
            LastStartTime = service.LastStartTime,
            ListeningPort = service.ListeningPort,
            RecentErrorsJson = Serialize(service.RecentErrors)
        };

    private static string Serialize(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static IReadOnlyList<string> Deserialize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string BuildDisplayName(string? displayName, string? fallbackUnitName)
    {
        var normalizedDisplayName = NormalizeText(displayName);
        if (!string.IsNullOrWhiteSpace(normalizedDisplayName))
        {
            return normalizedDisplayName;
        }

        var normalizedUnitName = NormalizeText(fallbackUnitName);
        return string.IsNullOrWhiteSpace(normalizedUnitName) ? "Unnamed service" : normalizedUnitName;
    }

    private static string BuildSummaryText(string? summary, string? fallbackUnitName)
    {
        var normalizedSummary = NormalizeText(summary);
        if (!string.IsNullOrWhiteSpace(normalizedSummary))
        {
            return normalizedSummary;
        }

        var normalizedUnitName = NormalizeText(fallbackUnitName);
        return string.IsNullOrWhiteSpace(normalizedUnitName)
            ? "No service summary is available."
            : $"No summary is available for {normalizedUnitName}.";
    }

    private static async Task<string> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        var result = await RunCommandResultAsync(fileName, arguments, cancellationToken, allowFailure);
        return result.ExitCode == 0 ? result.StandardOutput : string.Empty;
    }

    private static async Task<CommandResult> RunCommandResultAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr}");
        }

        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ServiceListRow(
        string UnitName,
        string LoadState,
        string ActiveState,
        string SubState,
        string Description);

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
