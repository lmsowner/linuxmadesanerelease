// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LinuxMadeSane.Web.Services;

// Upstream publishes portable binaries, not an Ubuntu package. Keep this optional
// tool in LMS's data directory; no root access or system-wide installation is needed.
internal static class LibreSpeedTool
{
    internal const string Version = "1.0.14";
    internal static async Task<string> EnsureAsync(string folder, HttpClient client, CancellationToken token)
    {
        var (architecture, checksum) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ("amd64", "89800767ac14085c78a20847ebea23340f6c14a78de0a15c2ac7db8b565c961f"),
            Architecture.Arm64 => ("arm64", "75e51a2494d03cb35a92ddbf862b40571a25a1526f3cf3dfa8b1d5d7bc622bd9"),
            _ => throw new InvalidOperationException("Automatic LibreSpeed setup supports Linux x64 and ARM64 hosts.")
        };
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, $"librespeed-cli-{Version}-{architecture}");
        if (File.Exists(executable)) return executable;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        byte[] archive;
        try { archive = await client.GetByteArrayAsync($"https://github.com/librespeed/speedtest-cli/releases/download/v{Version}/librespeed-cli_{Version}_linux_{architecture}.tar.gz", timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("LibreSpeed download exceeded its 90-second time limit."); }
        if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LibreSpeed download checksum did not match. Nothing was installed.");
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var temporary = executable + ".tmp";
        try
        {
            var found = false;
            while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                // Never extract paths or links supplied by an archive into the host filesystem.
                if (entry.Name is "LICENSE" && entry.DataStream is not null)
                {
                    await using var license = File.Create(Path.Combine(folder, $"librespeed-{Version}-LICENSE"));
                    await entry.DataStream.CopyToAsync(license, token);
                }
                if (entry.Name != "librespeed-cli" || entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;
                await using (var target = File.Create(temporary)) await entry.DataStream.CopyToAsync(target, token);
                found = true;
            }
            if (!found) throw new InvalidOperationException("LibreSpeed archive did not contain its executable.");
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temporary, executable);
            return executable;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
