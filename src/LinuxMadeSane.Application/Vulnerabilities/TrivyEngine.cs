// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Vulnerabilities;
namespace LinuxMadeSane.Application.Vulnerabilities;

public sealed record TrivyStatus(bool Installed,string Version,DateTimeOffset? DatabaseUpdatedAtUtc);
// The same engine and sanitised metadata format are used locally and by private Portal workers.
public sealed class TrivyEngine(ILinuxCommandRunner runner)
{
    // Pinned Trivy's supported template writer receives its Results list. Keep its
    // detected Target/Class/Type, but omit CVSS/reference blobs LMS never uses.
    // Sprig truncates bytes: retain 1024 bytes so LMS can apply its Unicode-safe
    // 256-character bound without losing text from multibyte descriptions.
    public const string CompactReportTemplate = """
{"Results":[{{range $i,$r := .}}{{if $i}},{{end}}{"Target":{{toJson $r.Target}},"Class":{{toJson $r.Class}},"Type":{{toJson $r.Type}},"Vulnerabilities":[{{range $j,$v := $r.Vulnerabilities}}{{if $j}},{{end}}{{dict "VulnerabilityID" $v.VulnerabilityID "PkgName" $v.PkgName "InstalledVersion" $v.InstalledVersion "FixedVersion" $v.FixedVersion "Status" $v.Status "CVSS" $v.CVSS "Severity" $v.Severity "Description" (trunc 1024 $v.Description) "Title" (trunc 1024 $v.Title) "PrimaryURL" $v.PrimaryURL "DataSource" $v.DataSource | toJson}}{{end}}]}{{end}}]}
""";
    private async Task<VulnerabilityActionSummary> BuildActionsAsync(OsPackageInventory inventory, IReadOnlyList<VulnerabilityFinding> findings, CancellationToken token)
    {
        var comparisons = new Dictionary<(string,string),bool>();
        var rows = new List<VulnerabilityRemediation>();
        foreach (var group in findings.GroupBy(f=>f.Package))
        {
            var installed=group.First().InstalledVersion;
            var candidate=inventory.UpdateCandidates.FirstOrDefault(p=>p.Package==group.Key && p.InstalledVersion==installed);
            var covered=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(candidate is not null)
                foreach(var finding in group.Where(f=>f.FixedVersion.Length>0))
                {
                    var key=(candidate.CandidateVersion,finding.FixedVersion);
                    if(!comparisons.TryGetValue(key,out var fixes))
                    {
                        var comparison=await runner.RunAsync(new("/usr/bin/dpkg",["--compare-versions",key.Item1,"ge",key.Item2],false,TimeSpan.FromSeconds(5),"Verify available vendor package fix"),false,token);
                        fixes=comparison.ExitCode==0; comparisons[key]=fixes;
                    }
                    if(fixes && finding.InstalledVersion==candidate.InstalledVersion)covered.Add(finding.Id);
                }
            var usage=inventory.Usage?.Packages.FirstOrDefault(p=>p.Package==group.Key);
            var context=inventory.Usage is null?"Runtime evidence unavailable.":usage is null?"No use observed in this process snapshot; boot and on-demand use are not assessed.":
                usage.Listeners.Count>0?"Listening: "+string.Join(", ",usage.Listeners):
                usage.Executables.Count>0?"Running: "+string.Join(", ",usage.Executables):"Loaded by a running process: "+string.Join(", ",usage.Libraries);
            if(usage?.DeletedFiles==true)context+=" A process still holds a removed/replaced file; its loaded version is unverified.";
            var action=candidate is not null ? covered.Count>0?"Install vendor update":"Package update; CVE fix not established":"Advisory evidence only";
            rows.Add(new(group.Key,installed,candidate?.CandidateVersion??"",action,group.Select(f=>f.Id).Distinct().Count(),covered.Count,context) { AdvisorySamples=group.OrderBy(f=>VulnerabilityGrouping.Rank(f.Severity)).ThenBy(f=>f.Id,StringComparer.Ordinal).Take(1).ToArray(),
                NetworkAdvisories=group.Where(VulnerabilityExposure.HasNetworkVector).Select(f=>f.Id).Distinct().Count(),
                NetworkAdvisorySamples=group.Where(VulnerabilityExposure.HasNetworkVector).OrderBy(f=>VulnerabilityGrouping.Rank(f.Severity)).ThenBy(f=>f.Id,StringComparer.Ordinal).Take(2).ToArray() });
        }
        return new(inventory.CapturedAtUtc,inventory.UpdatesCheckedAtUtc,inventory.RunningKernel,inventory.RebootRequired,rows.OrderByDescending(r=>r.CoveredByCandidate).ThenBy(r=>r.Package,StringComparer.Ordinal).ToArray()) { InventoryFingerprint = TrivyInventory.Fingerprint(inventory), Usage=inventory.Usage, AvailableUpdates=inventory.UpdateCandidates.Count, HasUnclassifiedAdvisories=findings.Any(f=>f.AttackVector=="Unknown"), MitigatedAdvisories=findings.Where(f=>f.PrerequisiteAbsent.Length>0).DistinctBy(f=>f.Id).ToArray() };
    }

    // Refresh runtime/configuration evidence without another Trivy job or charge.
    // The scan remains immutable; callers receive a derived view only when its
    // exact package/source versions and running kernel still match this host.
    public async Task<VulnerabilityReport> ReassessAsync(VulnerabilityReport report, OsPackageInventory live, CancellationToken token)
    {
        TrivyInventory.Validate(live);
        if(report.Actions?.InventoryFingerprint!=TrivyInventory.Fingerprint(live) || live.Usage is null)return report;
        var findings=report.Findings.Select(f=>VulnerabilityPrerequisites.Assess(f,live.Usage)).ToArray();
        return report with {Findings=findings,Actions=await BuildActionsAsync(live,findings,token)};
    }

    public const string PinnedVersion="0.76.1";
    public const string ManagedExecutable="/opt/linuxmadesane/trivy/current/trivy";
    private readonly SemaphoreSlim gate=new(1);
    public async Task<string?> ExecutableAsync(CancellationToken token)
    {
        if(File.Exists(ManagedExecutable))return ManagedExecutable;
        var found=await runner.RunAsync(new("/bin/sh",["-c","command -v trivy"],false,TimeSpan.FromSeconds(5),"Detect Trivy"){IsOptionalExternalTool=true},false,token);
        var path=found.StandardOutput.Trim();return found.ExitCode==0&&Path.IsPathFullyQualified(path)&&!path.Contains('\n')?path:null;
    }
    public async Task<TrivyStatus> StatusAsync(string cache,CancellationToken token)
    {
        var path=await ExecutableAsync(token);if(path is null)return new(false,"",null);
        var result=await RunAsync(path,["--cache-dir",cache,"--version","--format","json"],TimeSpan.FromSeconds(15),token);
        if(result.ExitCode!=0)throw new InvalidOperationException("Trivy could not report its version.");
        using var json=JsonDocument.Parse(result.StandardOutput);
        return new(true,json.RootElement.TryGetProperty("Version",out var v)?v.GetString()??"":"",DatabaseTime(cache));
    }
    public async Task RefreshDatabaseAsync(string cache,CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await RefreshCoreAsync(cache,token); }
        finally { gate.Release(); }
    }
    private async Task RefreshCoreAsync(string cache,CancellationToken token)
    {
        var path=await ExecutableAsync(token)??throw new InvalidOperationException("Install Trivy before updating its database.");
        Directory.CreateDirectory(cache);
        var result=await RunAsync(path,["image","--cache-dir",cache,"--download-db-only","--no-progress"],TimeSpan.FromMinutes(8),token);
        if(result.ExitCode!=0)throw new InvalidOperationException("Trivy vulnerability database update failed. Check scanner logs and outbound connectivity.");
    }
    public async Task<VulnerabilityReport> ScanAsync(Guid id,VulnerabilityScanMode mode,OsPackageInventory inventory,string cache,int refreshHours,CancellationToken token)
    {
        TrivyInventory.Validate(inventory);
        await gate.WaitAsync(token);
        var work=Path.Combine(Path.GetDirectoryName(cache)!,"jobs",Guid.NewGuid().ToString("N"));
        try
        {
            var path=await ExecutableAsync(token)??throw new InvalidOperationException("Trivy is not installed.");
            if(DatabaseTime(cache) is not {} updated || DateTimeOffset.UtcNow-updated>TimeSpan.FromHours(refreshHours))await RefreshCoreAsync(cache,token);
            Directory.CreateDirectory(work);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(work,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            await TrivyInventory.MaterialiseAsync(inventory,Path.Combine(work,"root"),token);
            var output=Path.Combine(work,"result.json");
            var template=Path.Combine(work,"report.tpl");await File.WriteAllTextAsync(template,CompactReportTemplate,token);
            var result=await RunAsync(path,["rootfs","--cache-dir",cache,"--cache-backend","memory","--skip-db-update","--scanners","vuln","--pkg-types","os","--parallel","1","--timeout","5m","--format","template","--template","@"+template,"--output",output,Path.Combine(work,"root")],TimeSpan.FromMinutes(6),token);
            if(result.ExitCode!=0)throw new InvalidOperationException("Trivy assessment failed. Previous findings are retained.");
            var info=new FileInfo(output);if(!info.Exists||info.Length==0)throw new InvalidDataException("Scanner produced no report. Previous results are retained.");
            if(info.Length>256L*1024*1024)throw new InvalidDataException("Compact scanner report exceeds the 256 MiB safety limit. Previous results are retained.");
            IReadOnlyList<VulnerabilityFinding> findings;
            await using(var input=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
                findings=await TrivyFindings.ParseAsync(input,inventory,token);
            findings=findings.Select(f=>VulnerabilityPrerequisites.Assess(f,inventory.Usage)).ToArray();
            var status=await StatusAsync(cache,token);
            var report=new VulnerabilityReport(id,mode,DateTimeOffset.UtcNow,inventory.CapturedAtUtc,status.DatabaseUpdatedAtUtc,status.Version,findings) { Actions = await BuildActionsAsync(inventory, findings, token) };
            // Large bounded reports can legitimately exceed 64 MiB (notably
            // hosts retaining several kernels). Count streamed JSON bytes without
            // allocating an additional full-size byte array solely to check it.
            using(var budget=new ReportBudgetStream())await JsonSerializer.SerializeAsync(budget,report,cancellationToken:token);
            return report;
        }
        finally
        {
            // Only exact scanner-owned files and empty directories are removed. Never traverse a host tree.
            try
            {
            foreach(var file in new[]{"result.json","report.tpl","root/etc/os-release","root/etc/lsb-release","root/etc/debian_version","root/var/lib/dpkg/status"})File.Delete(Path.Combine(work,file));
            foreach(var directory in new[]{"root/var/lib/dpkg","root/var/lib","root/var","root/etc","root",""})
            {var p=Path.Combine(work,directory);if(Directory.Exists(p))Directory.Delete(p);}
            }
            catch(IOException) { /* Only disposable scanner files remain; never traverse anything unexpected. */ }
            finally { gate.Release(); }
        }
    }
    private sealed class ReportBudgetStream:Stream
    {
        private long bytes;
        private void Count(int count){bytes+=count;if(bytes>256L*1024*1024)throw new InvalidDataException("Normalised scan results exceed the 256 MiB safety limit.");}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>bytes;public override long Position{get=>bytes;set=>throw new NotSupportedException();}
        public override void Flush(){} public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>Count(count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default){token.ThrowIfCancellationRequested();Count(buffer.Length);return ValueTask.CompletedTask;}
    }
    public static DateTimeOffset? DatabaseTime(string cache)
    {
        try {using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(cache,"db/metadata.json")));return d.RootElement.GetProperty("UpdatedAt").GetDateTimeOffset();}
        catch(Exception e) when(e is IOException or JsonException or KeyNotFoundException or FormatException){return null;}
    }
    private Task<LinuxCommandResult> RunAsync(string executable,IEnumerable<string> args,TimeSpan timeout,CancellationToken token)=>
        runner.RunAsync(new("/usr/bin/env",new[]{"GOMAXPROCS=2","GOMEMLIMIT=512MiB","nice","-n","10",executable}.Concat(args).ToArray(),false,timeout,"Trivy OS vulnerability assessment"),false,token);
}
