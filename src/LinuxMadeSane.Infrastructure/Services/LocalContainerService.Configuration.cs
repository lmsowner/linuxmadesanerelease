// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Security.Cryptography;
using System.Text.Json;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record ContainerConfigFile(string Mount, string Relative, bool Writable)
{
    public string Path => Relative.Length == 0 ? Mount : Mount.TrimEnd('/') + "/" + Relative;
}
public sealed record ContainerConfigText(string Content, string Revision, string Encoding);

public sealed partial class LocalContainerService
{
    private static readonly Lazy<string> ConfigHelper = new(() =>
    {
        using var stream = typeof(LocalContainerService).Assembly.GetManifestResourceStream(
            "LinuxMadeSane.Infrastructure.Services.HomeLab.container_config.py");
        using var reader = new StreamReader(stream ?? throw new InvalidOperationException("Configuration editor helper is missing."));
        return reader.ReadToEnd();
    });

    private async Task<IReadOnlyList<ContainerConfigMount>> CheckedMounts(string id, string action)
    {
        await AdminAsync(action, id);
        var node = await InspectAsync(id);
        await CheckOwnerAsync(node, allowUnregistered: true);
        var declaredApp = node["Config"]?["Labels"]?["com.linuxmadesane.homelab.app"] != null;
        return ConfigMounts(node).Where(m => declaredApp || IsConfigDestination(m.Destination)).ToList();
    }

    private static bool IsConfigDestination(string path) =>
        path.StartsWith("/etc/", StringComparison.Ordinal) ||
        path.Split('/').Any(part => part is "conf" or "config" or "configuration" or "conf.d") ||
        Path.GetFileName(path) is "Dockerfile" or "Caddyfile" or ".env" ||
        Path.GetExtension(path).ToLowerInvariant() is ".conf" or ".cfg" or ".ini" or ".json" or ".properties"
            or ".toml" or ".xml" or ".yaml" or ".yml" or ".config" or ".cnf" or ".env" or ".hcl";

    private async Task<string> ConfigIo(string operation, ContainerConfigMount mount, string relative = "",
        string? revision = null, byte[]? content = null)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(new { root = mount.HostPath, relative, operation, revision, content });
        var request = new LinuxCommandRequest("python3", ["-c", ConfigHelper.Value], false, TimeSpan.FromSeconds(15),
            $"Container configuration {operation}") { StandardInputBytes = input };
        var result = await runner.RunAsync(request, false);
        if (result.ExitCode != 0) throw new InvalidOperationException("Configuration file: " + result.StandardError.Trim());
        return result.StandardOutput.Trim();
    }

    public async Task<List<ContainerConfigFile>> ListConfigurationFilesAsync(string id)
    {
        var files = new List<ContainerConfigFile>();
        foreach (var mount in await CheckedMounts(id, "list configuration files"))
        {
            var listing = await ConfigIo("list", mount);
            foreach (var relative in JsonSerializer.Deserialize<List<string>>(listing) ?? [])
                files.Add(new(mount.Destination, relative, mount.Writable));
        }
        return files.OrderBy(f => f.Relative.Length == 0 ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    public async Task<ContainerConfigText> ReadConfigurationAsync(string id, ContainerConfigFile file)
    {
        var mount = (await CheckedMounts(id, "read configuration file")).SingleOrDefault(m => m.Destination == file.Mount)
            ?? throw new InvalidOperationException("The mount changed. Reopen configuration.");
        var bytes = Convert.FromBase64String(await ConfigIo("read", mount, file.Relative));
        var decoded = TextFileEncoding.Decode(bytes);
        if (decoded.Content.Contains('\0') || !bytes.AsSpan().SequenceEqual(TextFileEncoding.Encode(decoded.Content, decoded.EncodingName)))
            throw new InvalidOperationException("This file is not editable text.");
        return new(decoded.Content, Convert.ToHexStringLower(SHA256.HashData(bytes)), decoded.EncodingName);
    }

    public async Task<string> SaveConfigurationAsync(string id, ContainerConfigFile file, ContainerConfigText original, string content)
    {
        var mount = (await CheckedMounts(id, "save configuration file")).SingleOrDefault(m => m.Destination == file.Mount);
        if (mount == null) throw new InvalidOperationException("The configuration mount changed. Reopen configuration.");
        var bytes = TextFileEncoding.Encode(content, original.Encoding);
        await ConfigIo("write", mount, file.Relative, original.Revision, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
