// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LinuxMadeSane.Web.Services;

public sealed record PerformanceBenchmarkResult(string Profile, string ToolVersion, DateTimeOffset TestedAtUtc,
    string DiskFolder, string Filesystem, int Threads, double SingleCpu, double MultiCpu, double ReadMBps,
    double WriteMBps, double LoadBefore, string RawOutput);
public sealed record PerformanceBenchmarkStatus(bool Running, string Stage, string? Error, PerformanceBenchmarkResult? Result);
public sealed record BenchmarkCommandResult(int ExitCode, string Output, string Error);
public interface IBenchmarkProcessRunner
{
    Task<BenchmarkCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken token);
}
public sealed class BenchmarkProcessRunner : IBenchmarkProcessRunner
{
    public async Task<BenchmarkCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        info.Environment["LC_ALL"] = "C";
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                // sudo forwards TERM to its privileged child. Killing its wrapper with KILL can leave apt running.
                if (OperatingSystem.IsLinux() && executable == "sudo")
                {
                    var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                    signal.ArgumentList.Add("-TERM"); signal.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
                    using var sender = Process.Start(signal);
                    if (sender is not null) await sender.WaitForExitAsync(CancellationToken.None);
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException)
                    { throw new InvalidOperationException("The package installer could not be stopped. Check the host's package manager before trying again."); }
                }
                else
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (process.HasExited) { }
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        return new(process.ExitCode, await output, await error);
    }
}

// Only orchestration: every measurement comes from the packaged sysbench CPU/fileio tests.
public sealed class PerformanceBenchmarkService
{
    public const string Profile = "sysbench-1.0-cpu10000-direct1M-1G-3x10s-v1";
    public const long FileBytes = 1L << 30;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBenchmarkProcessRunner runner;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<PerformanceBenchmarkService> logger;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private PerformanceBenchmarkStatus status = new(false, "Not benchmarked", null, null);
    public string DefaultDiskFolder { get; }
    public string ResultFile { get; }

    public PerformanceBenchmarkService(IBenchmarkProcessRunner runner, IConfiguration config, IWebHostEnvironment environment,
        IHostApplicationLifetime lifetime, ILogger<PerformanceBenchmarkService> logger)
    {
        this.runner = runner; this.lifetime = lifetime; this.logger = logger;
        var db = new SqliteConnectionStringBuilder(config.GetConnectionString("LinuxMadeSane") ?? "Data Source=data/linuxmadesane.db");
        DefaultDiskFolder = Path.GetDirectoryName(Path.GetFullPath(db.DataSource, environment.ContentRootPath))!;
        ResultFile = Path.Combine(DefaultDiskFolder, "performance", "latest.json");
        try
        {
            if (File.Exists(ResultFile))
                status = status with { Result = ParseSaved(File.ReadAllText(ResultFile)), Stage = "Ready" };
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not read saved performance benchmark."); }
    }
    public PerformanceBenchmarkStatus GetStatus() { lock (gate) return status; }
    public bool Start(string diskFolder)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Benchmarks run on Linux LMS hosts.");
        var folder = Path.GetFullPath(diskFolder);
        if (!Directory.Exists(folder)) throw new InvalidOperationException("Choose an existing disk test folder.");
        lock (gate)
        {
            if (status.Running) return false;
            cancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            status = status with { Running = true, Stage = "Checking benchmark tools and disk space…", Error = null };
            _ = Task.Run(() => RunAsync(folder, cancellation.Token));
            return true;
        }
    }
    public void Cancel() { lock (gate) { if (status.Running) { status = status with { Stage = "Cancelling…" }; cancellation?.Cancel(); } } }
    private void Stage(string text) { lock (gate) status = status with { Stage = text }; }
    private async Task<string> Command(string executable, string[] args, string folder, CancellationToken token, int seconds = 300)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        BenchmarkCommandResult result;
        try { result = await runner.RunAsync(executable, args, folder, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException($"{executable} exceeded its {seconds}-second time limit."); }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{executable} failed: {result.Error.Trim()} {result.Output.Trim()}");
        return result.Output;
    }
    private async Task RunAsync(string folder, CancellationToken token)
    {
        string? work = null;
        try
        {
            var available = await runner.RunAsync("/bin/sh", ["-c", "command -v sysbench"], folder, token);
            if (available.ExitCode != 0)
            {
                Stage("Installing sysbench…");
                try
                {
                    await Command("sudo", ["-n", "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "-o", "DPkg::Lock::Timeout=60", "update"], folder, token);
                    await Command("sudo", ["-n", "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "-o", "DPkg::Lock::Timeout=60", "install", "-y", "sysbench"], folder, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { throw new InvalidOperationException("LMS could not install sysbench. Check this host's package manager and administrator access. " + ex.Message); }
            }
            var version = (await Command("sysbench", ["--version"], folder, token)).Trim();
            if (!Regex.IsMatch(version, @"sysbench 1\.0\.")) throw new InvalidOperationException("This comparison profile requires sysbench 1.0.x. Installed: " + version);
            var fsOutput = await Command("findmnt", ["-J", "-T", folder, "-o", "TARGET,SOURCE,FSTYPE"], folder, token);
            using var fs = JsonDocument.Parse(fsOutput);
            var mount = fs.RootElement.GetProperty("filesystems")[0];
            var filesystem = $"{mount.GetProperty("source").GetString()} ({mount.GetProperty("fstype").GetString()}) at {mount.GetProperty("target").GetString()}";
            if (mount.GetProperty("fstype").GetString() is "tmpfs" or "ramfs")
                throw new InvalidOperationException("This folder is on a RAM filesystem. Choose a folder on the disk you want to test.");
            var free = (await Command("df", ["-B1", "--output=avail", "--", folder], folder, token)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
            if (!long.TryParse(free, out var freeBytes) || freeBytes < 2_200_000_000L)
                throw new InvalidOperationException("The selected filesystem needs at least 2.2 GB free for the temporary disk test.");
            work = Path.Combine(folder, ".lms-benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var loadText = File.ReadAllText("/proc/loadavg").Split(' ')[0];
            var load = double.Parse(loadText, CultureInfo.InvariantCulture);
            var threads = Math.Max(1, Environment.ProcessorCount);
            var log = new System.Text.StringBuilder();
            async Task<double> Measure(string name, string[] args, Func<string, double> parse)
            {
                var values = new List<double>();
                for (var trial = 1; trial <= 3; trial++)
                {
                    Stage($"{name} — run {trial} of 3…");
                    var output = await Command("sysbench", args, work, token, 90);
                    log.AppendLine("sysbench " + string.Join(" ", args)).AppendLine(output);
                    values.Add(parse(output));
                }
                return Median(values);
            }
            var single = await Measure("Single-core CPU", CpuArguments(1), ParseCpu);
            var multi = await Measure($"Multi-core CPU ({threads} threads)", CpuArguments(threads), ParseCpu);
            Stage("Preparing the temporary disk test file…");
            log.AppendLine("sysbench fileio --file-num=1 --file-total-size=1G prepare").AppendLine(await Command("sysbench", ["fileio", "--file-num=1", "--file-total-size=1G", "prepare"], work, token));
            var write = await Measure("Disk write", DiskArguments("seqrewr"), x => ParseDisk(x, "written", "write"));
            var read = await Measure("Disk read", DiskArguments("seqrd"), x => ParseDisk(x, "read", "read"));
            var result = new PerformanceBenchmarkResult(Profile, version, DateTimeOffset.UtcNow, folder, filesystem,
                threads, single, multi, read, write, load, log.ToString());
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(ResultFile)!);
            var temporary = ResultFile + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result, JsonOptions), token);
            File.Move(temporary, ResultFile, overwrite: true);
            lock (gate) status = new(true, "Benchmark complete", null, result);
        }
        catch (OperationCanceledException) { lock (gate) status = status with { Stage = "Benchmark cancelled" }; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Performance benchmark failed.");
            lock (gate) status = status with { Stage = "Benchmark failed", Error = ex.Message };
        }
        finally
        {
            if (work is not null)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove benchmark scratch folder {Folder}.", work); lock (gate) status = status with { Error = $"Temporary benchmark files remain at {work}: {ex.Message}" }; }
            }
            try { File.Delete(ResultFile + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not remove temporary benchmark result."); }
            lock (gate) status = status with { Running = false };
        }
    }
    public static string[] CpuArguments(int threads) => ["cpu", "--cpu-max-prime=10000", $"--threads={threads}", "--time=10", "--events=0", "run"];
    public static string[] DiskArguments(string mode) => ["fileio", "--file-num=1", "--file-total-size=1G", "--file-block-size=1M", "--file-extra-flags=direct", "--file-io-mode=sync", "--file-fsync-freq=0", "--file-fsync-end=on", $"--file-test-mode={mode}", "--threads=1", "--time=10", "--events=0", "run"];
    public static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
    public static double ParseCpu(string output) => Number(output, @"events per second:\s*([0-9.]+)");
    public static double ParseDisk(string output, string legacyName, string modernName)
    {
        var legacy = Regex.Match(output, $@"{legacyName}, MiB/s:\s*([0-9.]+)");
        if (legacy.Success) return ValidNumber(legacy.Groups[1].Value) * 1.048576;
        return Number(output, $@"{modernName}:\s*IOPS=[0-9.]+\s*[0-9.]+ MiB/s \(([0-9.]+) MB/s\)");
    }
    private static double Number(string output, string pattern)
    {
        var match = Regex.Match(output, pattern);
        if (!match.Success) throw new InvalidOperationException("sysbench did not report the expected result. The benchmark has not been saved.");
        return ValidNumber(match.Groups[1].Value);
    }
    private static double ValidNumber(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number <= 0)
            throw new InvalidOperationException("sysbench reported an invalid or zero measurement.");
        return number;
    }
    public static PerformanceBenchmarkResult? ParseSaved(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;
        var result = JsonSerializer.Deserialize<PerformanceBenchmarkResult>(json, JsonOptions);
        if (result is null) return null;
        if (result.Profile != Profile || result.Threads < 1 || new[] { result.SingleCpu, result.MultiCpu, result.ReadMBps, result.WriteMBps }.Any(x => !double.IsFinite(x) || x <= 0))
            throw new InvalidOperationException("Saved benchmark uses an unsupported profile or contains invalid results. Run the benchmark again.");
        return result;
    }
}
