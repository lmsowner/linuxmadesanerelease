// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Infrastructure;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services.Infrastructure;

public sealed class NutUpsManagementService(LinuxMadeSaneDbContext database, ILinuxCommandRunner runner,
    IServiceManagementService services, IHomeLabService homeLab, ILocalSystemMaintenanceService maintenance) : IUpsManagementService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? powerSource;
    private static DateTimeOffset? onBatterySince;
    private static readonly HashSet<string> CompletedActions = [];
    private static int consecutiveBatteryObservations;
    private Task<LinuxCommandResult> Run(string executable, string[] args, bool sudo, CancellationToken token, byte[]? input = null) =>
        runner.RunAsync(new(executable, args, sudo, TimeSpan.FromSeconds(25), "NUT UPS management") { StandardInputBytes = input, IsOptionalExternalTool = !sudo }, false, token);
    private async Task<T> Read<T>(string key, T fallback, CancellationToken token)
    { var row = await database.InfrastructureStates.FindAsync([key], token); return row is null ? fallback : JsonSerializer.Deserialize<T>(row.Json) ?? fallback; }
    private async Task Write<T>(string key, T value, CancellationToken token)
    {
        var row = await database.InfrastructureStates.FindAsync([key], token);
        if (row is null) { row = new() { Key = key }; database.InfrastructureStates.Add(row); }
        row.Json = JsonSerializer.Serialize(value); row.UpdatedAtUtc = DateTimeOffset.UtcNow; await database.SaveChangesAsync(token);
    }
    public async Task<UpsWorkspace> GetAsync(bool scanHardware = false, CancellationToken token = default)
    {
        var policy = await Read("ups-policy", new UpsPolicy(), token);
        var list = await Run("upsc", ["-l", "localhost"], false, token);
        var config = await Run("python3", ["-c", "import pathlib; p=pathlib.Path('/etc/nut/ups.conf'); print(p.read_text() if p.exists() else '',end='')"], true, token);
        var scan = scanHardware ? await Run("nut-scanner", ["-U"], true, token) : null;
        return new(policy, list.ExitCode == 0 ? list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim() + "@localhost").ToArray() : [],
            config.ExitCode == 0 ? config.StandardOutput : "Unable to read existing NUT configuration: " + config.StandardError,
            await Read<List<string>>("ups-events", [], token), scan is null ? "" : scan.ExitCode == 0 ? scan.StandardOutput : "Hardware scan unavailable: " + scan.StandardError);
    }
    public async Task<UpsSnapshot> InspectAsync(string source, CancellationToken token = default)
    {
        ValidateSource(source);
        var result = await Run("upsc", [source], false, token);
        var values = new Dictionary<string, string>();
        if (result.ExitCode == 0)
            foreach (var line in result.StandardOutput.Split('\n'))
            { var fields = line.Split(':', 2); if (fields.Length == 2) values[fields[0].Trim()] = fields[1].Trim(); }
        var available = result.ExitCode == 0 && values.ContainsKey("ups.status") && !values["ups.status"].Contains("DATASTALE", StringComparison.Ordinal);
        return new(source, available, values, available ? "NUT status read successfully." : "Cannot read current UPS status. Check the NUT server, UPS name and connection: " + result.StandardError, DateTimeOffset.UtcNow);
    }
    public async Task SavePolicyAsync(UpsPolicy policy, CancellationToken token = default)
    {
        ValidateSource(policy.Source);
        if (policy.StopContainersAfterMinutes is < 1 or > 1440 || policy.StopServicesAfterMinutes is < 1 or > 1440 || policy.ShutdownBelowPercent is < 1 or > 95)
            throw new InvalidOperationException("Use delays between 1 and 1440 minutes and a battery threshold between 1% and 95%.");
        if (policy.ShutdownHost && !policy.ShutdownConfirmed) throw new InvalidOperationException("Explicitly confirm permission to shut down this host.");
        foreach (var name in policy.Services)
            if (!Regex.IsMatch(name, "^[a-zA-Z0-9_.@:-]{1,128}$") || name.Contains("linux-made-sane", StringComparison.OrdinalIgnoreCase) || name.StartsWith("nut-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select valid non-essential services; LMS and NUT cannot be stopped by this policy.");
        var installations = (await homeLab.GetWorkspaceSnapshotAsync(token)).Installations;
        if (policy.ContainerIds.Any(id => !installations.Any(item => item.Id == id))) throw new InvalidOperationException("A selected Docker workload no longer exists.");
        if (policy.Enabled && !(await InspectAsync(policy.Source, token)).Available) throw new InvalidOperationException("Test a working NUT connection before enabling this policy.");
        await Gate.WaitAsync(token);
        try { await Write("ups-policy", policy, token); powerSource = null; onBatterySince = null; CompletedActions.Clear(); consecutiveBatteryObservations = 0; }
        finally { Gate.Release(); }
    }
    public static void ValidateSource(string source)
    {
        if (!Regex.IsMatch(source, @"^[a-zA-Z0-9_.-]+@(?:[a-zA-Z0-9_.-]+|\[[0-9a-fA-F:]+\])(?::[0-9]{1,5})?$"))
            throw new InvalidOperationException("Use UPS name@server, for example apc@nas.local or apc@192.168.1.20:3493.");
        var port = Regex.Match(source, @":([0-9]+)$");
        if (port.Success && (!int.TryParse(port.Groups[1].Value, out var number) || number is < 1 or > 65535))
            throw new InvalidOperationException("Use a NUT server port between 1 and 65535.");
    }
    public async Task ConfigureLocalAsync(string name, string driver, string port, bool adoptExisting, CancellationToken token = default)
    {
        if (!adoptExisting) throw new InvalidOperationException("Allow LMS management before modifying NUT configuration.");
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9_.-]{1,64}$") || !Regex.IsMatch(driver, "^[a-zA-Z0-9_-]{1,64}$") || port != "auto")
            throw new InvalidOperationException("The local setup supports a detected USB driver with port auto. Existing serial and advanced configurations are preserved.");
        var scan = await Run("nut-scanner", ["-U"], true, token);
        if (scan.ExitCode != 0 || !Regex.IsMatch(scan.StandardOutput, "(?m)^\\s*driver\\s*=\\s*\"?" + Regex.Escape(driver) + "\"?\\s*$"))
            throw new InvalidOperationException("This driver was not returned by the local USB scan. LMS will not guess a driver.");
        if (Regex.Matches(scan.StandardOutput, @"(?m)^\s*\[[^\]]+\]\s*$").Count > 1)
            throw new InvalidOperationException("Multiple USB UPS devices were detected. Configure explicit serial/device matching in NUT before LMS management; auto selection could choose the wrong UPS.");
        await Gate.WaitAsync(token);
        var staging = "/etc/nut/.lms-" + Guid.NewGuid().ToString("N");
        bool activated = false;
        string driverUnit = "";
        var previous = new List<ServiceState>();
        try
        {
            var prepared = await Run("python3", ["-c", Prepare, staging, name, driver], true, token);
            if (prepared.ExitCode != 0) throw new InvalidOperationException("NUT configuration was not changed: " + prepared.StandardError);
            var test = await Run("env", ["NUT_CONFPATH=" + staging, "upsdrvctl", "-t", "start", name], true, token);
            if (test.ExitCode != 0) throw new InvalidOperationException("NUT rejected the candidate: " + test.StandardError);
            var template = await Run("systemctl", ["cat", "nut-driver@.service"], false, token);
            driverUnit = template.ExitCode == 0 ? "nut-driver@" + name : "nut-driver";
            previous.AddRange(await services.InspectAsync([driverUnit, "nut-server"], token));
            if (previous.Any(unit => unit.IsMasked)) throw new InvalidOperationException("NUT service is masked. Review the host service settings before enabling it.");
            var activate = await Run("python3", ["-c", Activate, staging], true, token);
            if (activate.ExitCode != 0) throw new InvalidOperationException("Cannot activate NUT configuration: " + activate.StandardError);
            activated = true;
            foreach (var unit in previous)
                await Action(unit.Name, unit.IsActive ? ServiceActionKind.Restart : ServiceActionKind.Start, token);
            var status = await InspectAsync(name + "@localhost", token);
            if (!status.Available) throw new InvalidOperationException(status.Message);
            foreach (var unit in previous) await Action(unit.Name, ServiceActionKind.Enable, token);
            await Event("Configured local UPS " + name + "; backups are in " + staging + ". No shutdown policy was enabled.", token);
        }
        catch (Exception e) when (activated)
        {
            var restore = await Run("python3", ["-c", Rollback, staging], true, CancellationToken.None);
            if (restore.ExitCode != 0) throw new InvalidOperationException($"NUT setup failed: {e.Message}. Rollback failed; backups: {staging}. {restore.StandardError}", e);
            foreach (var unit in previous)
            {
                await Action(unit.Name, unit.IsActive ? ServiceActionKind.Restart : ServiceActionKind.Stop, CancellationToken.None);
                await Action(unit.Name, unit.IsEnabled ? ServiceActionKind.Enable : ServiceActionKind.Disable, CancellationToken.None);
            }
            throw new InvalidOperationException("NUT setup failed. Previous configuration restored: " + e.Message, e);
        }
        finally { Gate.Release(); }
    }
    private async Task Action(string unit, ServiceActionKind action, CancellationToken token)
    {
        var logs = await services.ApplyActionsAsync([new(action, unit, "UPS operation", false, "")], false, token);
        if (logs.Any(log => log.Level == OperationLogLevel.Error)) throw new InvalidOperationException(string.Join("; ", logs.Select(log => log.Message + " " + log.StandardError)));
    }
    private async Task Event(string message, CancellationToken token)
    {
        var events = await Read<List<string>>("ups-events", [], token); events.Insert(0, DateTimeOffset.UtcNow.ToString("O") + " " + message);
        await Write("ups-events", events.Take(100).ToList(), token);
    }
    public async Task MonitorAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var policy = await Read("ups-policy", new UpsPolicy(), token);
            if (!policy.Enabled) return;
            var snapshot = await InspectAsync(policy.Source, token);
            if (!snapshot.Available) { consecutiveBatteryObservations = 0; onBatterySince = null; return; }
            var status = snapshot.Values["ups.status"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (powerSource != policy.Source) { powerSource = policy.Source; onBatterySince = null; CompletedActions.Clear(); consecutiveBatteryObservations = 0; }
            if (!status.Contains("OB"))
            {
                if (onBatterySince.HasValue && status.Contains("OL")) await Event("Mains power restored; outage actions reset. Stopped workloads remain stopped for review.", token);
                onBatterySince = null; consecutiveBatteryObservations = 0; CompletedActions.Clear(); return;
            }
            consecutiveBatteryObservations++;
            if (!onBatterySince.HasValue) { onBatterySince = DateTimeOffset.UtcNow; await Event("UPS reports on battery: " + policy.Source, token); }
            if (consecutiveBatteryObservations < 2) return;
            var minutes = (DateTimeOffset.UtcNow - onBatterySince.Value).TotalMinutes;
            foreach (var id in policy.ContainerIds.Where(_ => minutes >= policy.StopContainersAfterMinutes))
            {
                var key = "container:" + id;
                if (CompletedActions.Contains(key)) continue;
                var result = await homeLab.ExecuteAsync(id, HomeLabLifecycleAction.Stop, token);
                if (!result.Succeeded) throw new InvalidOperationException("Could not stop UPS-selected workload: " + result.Summary);
                CompletedActions.Add(key); await Event("Stopped selected Docker workload " + id, token);
            }
            foreach (var name in policy.Services.Where(_ => minutes >= policy.StopServicesAfterMinutes))
            {
                var key = "service:" + name; if (CompletedActions.Contains(key)) continue;
                await Action(name, ServiceActionKind.Stop, token); CompletedActions.Add(key); await Event("Stopped selected service " + name, token);
            }
            if (policy.ShutdownHost && policy.ShutdownConfirmed && snapshot.Values.TryGetValue("battery.charge", out var battery) &&
                double.TryParse(battery, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent) &&
                percent is >= 0 and <= 100 && percent < policy.ShutdownBelowPercent && !CompletedActions.Contains("shutdown"))
            {
                await Event("Battery below configured threshold while on battery. Requesting graceful host shutdown.", token);
                await maintenance.ShutdownAsync(token); CompletedActions.Add("shutdown");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Event("UPS policy action failed: " + e.Message + ". Check the selected workload/service before retrying.", CancellationToken.None);
            throw;
        }
        finally { Gate.Release(); }
    }
    private const string Prepare = """
        import pathlib,sys,re,hashlib,json,os
        stage,name,driver=sys.argv[1:]; root=pathlib.Path('/etc/nut'); p=pathlib.Path(stage); p.mkdir(mode=0o700)
        manifest={}
        for file in ('ups.conf','nut.conf'):
            live=root/file
            if live.is_symlink(): raise RuntimeError('Refusing symbolic-link configuration')
            text=live.read_bytes().decode('utf-8') if live.exists() else ''
            if file=='ups.conf':
                if re.search(r'^\s*\['+re.escape(name)+r'\]\s*(?:#.*)?$',text,re.M): raise RuntimeError('UPS name already exists; existing configuration is preserved')
                candidate=text+'\n['+name+']\n    driver = '+driver+'\n    port = auto\n'
            else:
                modes=re.findall(r'^\s*MODE\s*=\s*([^\s#]+)\s*(?:#.*)?$',text,re.M)
                if modes and modes[-1] not in ('none','standalone','netserver'): raise RuntimeError('Existing NUT mode requires manual review')
                candidate=re.sub(r'^(\s*MODE\s*=\s*)none(?=\s*(?:#.*)?$)', r'\g<1>standalone',text,flags=re.M) if modes else text+'\nMODE=standalone\n'
            for path,value in ((p/(file+'.backup'),text),(p/file,candidate)):
                with open(path,'xb') as output: os.fchmod(output.fileno(),0o600); output.write(value.encode()); output.flush(); os.fsync(output.fileno())
            manifest[file]={'original':hashlib.sha256(text.encode()).hexdigest(),'candidate':hashlib.sha256(candidate.encode()).hexdigest(),'existed':live.exists()}
        with open(p/'manifest.json','xb') as output: os.fchmod(output.fileno(),0o600); output.write(json.dumps(manifest).encode()); output.flush(); os.fsync(output.fileno())
        for path in (p,p.parent): fd=os.open(path,os.O_DIRECTORY); os.fsync(fd); os.close(fd)
        """;
    private const string Activate = """
        import pathlib,sys,json,hashlib,os,shutil,uuid
        p=pathlib.Path(sys.argv[1]); root=pathlib.Path('/etc/nut'); manifest=json.loads((p/'manifest.json').read_text())
        for file,v in manifest.items():
            live=root/file; data=live.read_bytes() if live.exists() else b''
            if live.is_symlink() or hashlib.sha256(data).hexdigest()!=v['original']: raise RuntimeError('Configuration changed concurrently')
        changed=[]
        try:
            for file,v in manifest.items():
                live=root/file; candidate=root/(file+'.lms-new-'+uuid.uuid4().hex)
                with open(candidate,'xb') as output: output.write((p/file).read_bytes()); output.flush(); os.fsync(output.fileno())
                if live.exists(): st=live.stat(); os.chown(candidate,st.st_uid,st.st_gid); os.chmod(candidate,st.st_mode & 0o777)
                else: os.chown(candidate,0,root.stat().st_gid); os.chmod(candidate,0o640)
                os.replace(candidate,live); changed.append(file)
            fd=os.open(root,os.O_DIRECTORY); os.fsync(fd); os.close(fd)
        except:
            for file in reversed(changed):
                live=root/file
                if hashlib.sha256(live.read_bytes()).hexdigest()!=manifest[file]['candidate']: raise RuntimeError('Activation failed and another writer changed configuration; backups at '+str(p))
                if not manifest[file]['existed']: live.unlink(); continue
                st=live.stat(); temporary=root/(file+'.lms-recover-'+uuid.uuid4().hex)
                with open(temporary,'xb') as output: output.write((p/(file+'.backup')).read_bytes()); output.flush(); os.fsync(output.fileno())
                os.chown(temporary,st.st_uid,st.st_gid); os.chmod(temporary,st.st_mode & 0o777); os.replace(temporary,live)
            fd=os.open(root,os.O_DIRECTORY); os.fsync(fd); os.close(fd)
            raise
        """;

    private const string Rollback = """
        import pathlib,sys,json,hashlib,os,uuid
        p=pathlib.Path(sys.argv[1]); root=pathlib.Path('/etc/nut'); manifest=json.loads((p/'manifest.json').read_text())
        for file,v in manifest.items():
            live=root/file
            if live.is_symlink() or hashlib.sha256(live.read_bytes()).hexdigest()!=v['candidate']: raise RuntimeError('Live configuration changed; refusing overwrite')
        for file in manifest:
            live=root/file
            if not manifest[file]['existed']: live.unlink(); continue
            temp=root/(file+'.lms-restore-'+uuid.uuid4().hex); st=live.stat()
            with open(temp,'xb') as output: output.write((p/(file+'.backup')).read_bytes()); output.flush(); os.fsync(output.fileno())
            os.chown(temp,st.st_uid,st.st_gid); os.chmod(temp,st.st_mode & 0o777); os.replace(temp,live)
        fd=os.open(root,os.O_DIRECTORY); os.fsync(fd); os.close(fd)
        """;
}

public sealed class UpsPolicyMonitor(IServiceScopeFactory scopes, ILogger<UpsPolicyMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IUpsManagementService>().MonitorAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning(e, "UPS policy monitoring failed; no further power actions were taken this cycle"); }
        }
    }
}
