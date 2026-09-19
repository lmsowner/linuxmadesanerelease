// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record HttpServiceDiscoveryStorageSettings(string RootDirectory)
{
    public string CachePath => Path.Combine(RootDirectory, "http-services-cache.json");
    public string ScanCheckpointsPath => Path.Combine(RootDirectory, "http-service-scan-checkpoints.json");

    internal static HttpServiceDiscoveryStorageSettings CreatePersistent(
        string databaseDirectory,
        string contentRootPath)
    {
        var persistentRoot = Path.Combine(databaseDirectory, "http-service-discovery");
        var legacyRoot = Path.Combine(contentRootPath, "data", "http-service-discovery");
        MigrateLegacyFiles(legacyRoot, persistentRoot);
        return new HttpServiceDiscoveryStorageSettings(persistentRoot);
    }

    private static void MigrateLegacyFiles(string legacyRoot, string persistentRoot)
    {
        if (Path.GetFullPath(legacyRoot).Equals(Path.GetFullPath(persistentRoot), StringComparison.Ordinal) ||
            !Directory.Exists(legacyRoot))
        {
            return;
        }

        Directory.CreateDirectory(persistentRoot);
        foreach (var sourcePath in Directory.EnumerateFiles(legacyRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var destinationPath = Path.Combine(persistentRoot, Path.GetFileName(sourcePath));
            if (!File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath);
            }
        }
    }
}
