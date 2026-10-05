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
