// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Security;
namespace LinuxMadeSane.Infrastructure.Services.SshForwards;
public sealed record SshForwardStorageSettings(string DirectoryPath);
public sealed class SshForwardStore(SshForwardStorageSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath => settings.DirectoryPath;
    private string PathName => Path.Combine(DirectoryPath, "ssh-forwards.json");
    private readonly SemaphoreSlim diagnosticGate = new(1, 1);
    private string DiagnosticPath(Guid id) => Path.Combine(DirectoryPath, "ssh-forward-" + id.ToString("N") + "-diagnostics.json");
    public async Task<IReadOnlyList<SshForwardDiagnostic>> ReadDiagnosticsAsync(Guid id, CancellationToken token)
    {
        var path = DiagnosticPath(id);
        return File.Exists(path) ? JsonSerializer.Deserialize<SshForwardDiagnostic[]>(await File.ReadAllTextAsync(path, token), Json) ?? [] : [];
    }
    public async Task ClearDiagnosticsAsync(Guid id, CancellationToken token)
    {
        await diagnosticGate.WaitAsync(token);
        try { File.Delete(DiagnosticPath(id)); }
        finally { diagnosticGate.Release(); }
    }
    public async Task AppendDiagnosticAsync(Guid id, SshForwardDiagnostic entry, CancellationToken token)
    {
        await diagnosticGate.WaitAsync(token);
        var temporary = DiagnosticPath(id) + "." + Guid.NewGuid().ToString("N");
        try
        {
            // Keep the latest detail for each event type, rather than an accumulating retry log.
            entry = entry with { Detail = entry.Detail.Length > 4000 ? entry.Detail[..4000] : entry.Detail };
            var entries = (await ReadDiagnosticsAsync(id, token)).Where(x => x.State != entry.State).Append(entry).TakeLast(10).ToArray();
            Directory.CreateDirectory(DirectoryPath);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries, Json), token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, DiagnosticPath(id), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); diagnosticGate.Release(); }
    }
    public async Task<IReadOnlyList<SshPortForward>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(PathName)) return [];
        return JsonSerializer.Deserialize<SshPortForward[]>(await File.ReadAllTextAsync(PathName, token), Json)
            ?? throw new InvalidOperationException("The SSH forwards file is unreadable. Existing settings have not been replaced.");
    }
    public async Task WriteAsync(IEnumerable<SshPortForward> rules, CancellationToken token)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = PathName + "." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(rules, Json), token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, PathName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
