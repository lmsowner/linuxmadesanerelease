// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Diagnostics;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
namespace LinuxMadeSane.Infrastructure.Services.SshForwards;
public interface ISshForwardProcess : IAsyncDisposable
{
    bool HasExited { get; }
    int ProcessId { get; }
    int? AllocatedListenPort { get; }
    string Failure { get; }
    string? TrafficError { get; }
    Task<bool> IsConnectedAsync(CancellationToken token);
}
public interface ISshForwardProcessFactory
{
    Task<ISshForwardProcess> StartAsync(SshPortForward rule, SavedConnectionCredential credential, CancellationToken token, bool loginOnly = false);
}
public sealed class SshForwardProcessFactory(SshForwardStore store, ILinuxCommandRunner commands) : ISshForwardProcessFactory
{
    private static readonly SemaphoreSlim Packages = new(1, 1);
    private async Task EnsureToolsAsync(CancellationToken token)
    {
        if (File.Exists("/usr/bin/ssh") && File.Exists("/usr/bin/python3")) return;
        await Packages.WaitAsync(token);
        try
        {
            if (File.Exists("/usr/bin/ssh") && File.Exists("/usr/bin/python3")) return;
            foreach (var args in new[] { new[] { "update" }, new[] { "install", "--yes", "openssh-client", "python3" } })
            {
                var result = await commands.RunAsync(new("apt-get", args, true, TimeSpan.FromMinutes(5), "Install missing SSH forwarding support on LMS"), false, token);
                if (result.ExitCode != 0) throw new InvalidOperationException("On this LMS host: automatic installation of SSH forwarding support failed. Check package access and LMS's sudo permissions.");
            }
            if (!File.Exists("/usr/bin/ssh") || !File.Exists("/usr/bin/python3")) throw new InvalidOperationException("On this LMS host: SSH forwarding support is still unavailable after installation.");
        }
        finally { Packages.Release(); }
    }
    public async Task<ISshForwardProcess> StartAsync(SshPortForward rule, SavedConnectionCredential credential, CancellationToken token, bool loginOnly = false)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Persistent SSH forwards run on the Linux LMS host.");
        await EnsureToolsAsync(token);
        Directory.CreateDirectory(store.DirectoryPath);
        File.SetUnixFileMode(store.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The identity is a Linux anonymous memory file; passwords/passphrases remain in a private in-memory broker.
        // This directory contains helper code and sockets only. All secret material stays in memory.
        // Use a service-user-writable location: CE normally runs as linuxmadesane, not root.
        var runtime = Path.Combine(Path.GetTempPath(), "lms-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? process = null;
        try
        {
            var launcher = Path.Combine(runtime, "launch.py"); var askpass = Path.Combine(runtime, "askpass");
            await File.WriteAllTextAsync(launcher, Resource("forward-launcher.py"), token);
            await File.WriteAllTextAsync(askpass, Resource("forward-askpass.py"), token);
            File.SetUnixFileMode(askpass, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var control = Path.Combine(runtime, "control");
            var args = SshForwardDefinition.Arguments(rule, credential.PrivateKey.Length > 0 ? "LMS_MEMORY_KEY" : "",
                Path.Combine(store.DirectoryPath, "known_hosts"), control).ToList();
            if (loginOnly) { var index = args.FindIndex(arg => arg is "-L" or "-R" or "-D"); args.RemoveRange(index, 2); }
            var start = new ProcessStartInfo("/usr/bin/python3") { UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(launcher);
            process = Process.Start(start) ?? throw new InvalidOperationException("SSH could not be started on the LMS host.");
            var handle = new SshForwardProcess(process, runtime, control, rule, credential);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { args, supervisorPid = Environment.ProcessId, socket = Path.Combine(runtime, "ask.sock"),
                askpass, privateKey = credential.PrivateKey, password = credential.Password, passphrase = credential.Passphrase }).AsMemory(), token);
            process.StandardInput.Close();
            return handle;
        }
        catch
        {
            if (process is not null) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
            Directory.Delete(runtime, true); throw;
        }
    }
    public static void ValidateEmbeddedResources()
    {
        _ = Resource("forward-launcher.py");
        _ = Resource("forward-askpass.py");
    }
    private static string Resource(string name)
    {
        using var stream = typeof(SshForwardProcessFactory).Assembly.GetManifestResourceStream(
            "LinuxMadeSane.Infrastructure.Services.SshForwards." + name) ?? throw new InvalidOperationException($"On this LMS host: the installed package is missing SSH forwarding support ({name}). Update LMS to a complete CE package; changing credentials will not fix this packaging error.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
internal sealed class SshForwardProcess : ISshForwardProcess
{
    private readonly Process process;
    private readonly string runtime, control;
    private readonly SshPortForward rule;
    private readonly string[] redactions;
    private readonly Task errorTask, outputTask;
    private string failure = "The SSH process ended. Check the saved credentials, server forwarding policy and destination.";
    public SshForwardProcess(Process process, string runtime, string control, SshPortForward rule, SavedConnectionCredential credential)
    {
        this.process = process; this.runtime = runtime; this.control = control; this.rule = rule;
        redactions = [credential.Password, credential.Passphrase, credential.PrivateKey];
        errorTask = ReadErrorsAsync(); outputTask = process.StandardOutput.ReadToEndAsync();
    }
    public bool HasExited => process.HasExited;
    public int ProcessId => process.Id;
    public int? AllocatedListenPort { get; private set; }
    public string Failure => failure;
    public string? TrafficError { get; private set; }
    private async Task ReadErrorsAsync()
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (line.Contains("Permanently added", StringComparison.OrdinalIgnoreCase)) continue;
            var allocated = System.Text.RegularExpressions.Regex.Match(line, @"Allocated port (\d+)");
            if (allocated.Success) { AllocatedListenPort = int.Parse(allocated.Groups[1].Value); continue; }
            foreach (var secret in redactions.Where(value => value.Length > 0)) line = line.Replace(secret, "[redacted]", StringComparison.Ordinal);
            line = line.Replace(runtime, "[LMS runtime]", StringComparison.Ordinal);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"/proc/\d+/fd/\d+", "[in-memory key]");
            if (line.Contains("open failed", StringComparison.OrdinalIgnoreCase) || line.Contains("connect failed", StringComparison.OrdinalIgnoreCase))
                TrafficError = line.Length > 500 ? line[..500] : line;
            if (line.Length > 0) failure = line.Length > 500 ? line[..500] : line;
        }
    }
    public async Task<bool> IsConnectedAsync(CancellationToken token)
    {
        if (process.HasExited || !File.Exists(control)) return false;
        var start = new ProcessStartInfo("/usr/bin/ssh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-F", "/dev/null", "-S", control, "-O", "check", "--", rule.Server }) start.ArgumentList.Add(arg);
        using var check = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var output = check.StandardOutput.ReadToEndAsync(); var error = check.StandardError.ReadToEndAsync();
        try { await check.WaitForExitAsync(timeout.Token); return check.ExitCode == 0; }
        catch { if (!check.HasExited) check.Kill(true); return false; }
        finally { await output; await error; }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync(); await errorTask; await outputTask; process.Dispose();
        if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
    }
}
