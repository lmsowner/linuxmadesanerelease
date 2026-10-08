// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Infrastructure.Services.SshForwards;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Infrastructure.Services.Infrastructure;

public static class LmsConfigurationRecovery
{
    public static void ValidateEmbeddedResources()
    {
        _ = Script;
        _ = FullSystemScript;
    }

    internal static string Script => ReadScript("lms_config_recovery.py");
    internal static string FullSystemScript => ReadScript("lms_full_system.py");
    private static string ReadScript(string name)
    {
        using var stream = typeof(LmsConfigurationRecovery).Assembly.GetManifestResourceStream(
            "LinuxMadeSane.Infrastructure.Services.Infrastructure.Recovery." + name)
            ?? throw new InvalidOperationException($"This LMS installation is missing the packaged recovery tool {name}. Reinstall or update LMS; installing Linux packages will not repair this application packaging error.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static object Specification(IServiceProvider provider, InfrastructureHostPaths paths, string dataDirectory, string databaseFileName)
    {
        var locations = new List<object>();
        void Add(string id, string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            source = Path.GetFullPath(source);
            var dataRelative = Path.GetRelativePath(dataDirectory, source);
            var appRelative = Path.GetRelativePath(paths.ApplicationDirectory, source);
            if (dataRelative == "." || appRelative == ".")
                throw new InvalidOperationException("Persistent configuration storage must have a dedicated directory, not the entire LMS data or application root.");
            var category = !dataRelative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(dataRelative) ? "data" :
                !appRelative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(appRelative) ? "application" : "custom";
            locations.Add(new { id, source, @base = category, relative = category == "data" ? dataRelative : category == "application" ? appRelative : "" });
        }
        Add("share-credentials", provider.GetService<ShareMountStorageSettings>()?.CredentialsDirectory);
        Add("ssh-forwards", provider.GetService<SshForwardStorageSettings>()?.DirectoryPath);
        Add("port-forwards", provider.GetService<PortForwardStorageSettings>()?.DirectoryPath);
        Add("local-ai-peers", provider.GetService<LocalAiPeerSharingStorageSettings>()?.RootDirectory);
        Add("http-discovery", provider.GetService<HttpServiceDiscoveryStorageSettings>()?.RootDirectory);
        Add("rdp-configuration-history", provider.GetService<RdpOptimizerStorageSettings>()?.RootDirectory);
        Add("home-lab-catalog", Path.Combine(dataDirectory, "home-lab-catalog"));
        return new
        {
            hostname = Environment.MachineName,
            edition = ReadMarker(paths.ApplicationDirectory, "edition.txt"),
            version = ReadMarker(paths.ApplicationDirectory, "version.txt"),
            sourceCommit = ReadMarker(paths.ApplicationDirectory, "source-commit.txt"),
            databaseFileName,
            sourceDataDirectory = dataDirectory,
            sourceApplicationDirectory = paths.ApplicationDirectory,
            sourceKeysDirectory = paths.ProtectionKeyDirectory,
            hostConfigurationSource = paths.HostConfigurationDirectory,
            locations,
            coverage = new[] { "LMS database and credential protection keys", "Application configuration", "Registered persistent LMS configuration stores", "Host configuration archive and users' SSH configuration", "Package, service, network and disk inventory" },
            exclusions = new[] { "Operating system image and bootloader", "Docker volumes and application data unless explicitly selected", "User home files unless explicitly selected", "Temporary sessions, mounts and pending security trials" },
            hostReviewRequired = true
        };
    }

    private static string ReadMarker(string root, string name)
    {
        var path = Path.Combine(root, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
    }

    internal static async Task WriteRecoveryFilesAsync(string scratch, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(scratch, "recover-lms-config.py"), Script, token);
        await File.WriteAllTextAsync(Path.Combine(scratch, "RECOVERY.txt"), """
            LMS CONFIGURATION RECOVERY

            This bundle is stored inside your restic repository. If password protection was disabled, anyone with access to the repository can restore it.
            For password-protected storage, keep the repository password independently of this LMS host. Without it, the
            repository cannot be opened after the original host is lost. Restored files are
            decrypted: keep the recovery directory private and remove it after recovery.

            WHAT THIS RECOVERS
            LMS settings, database, protected credentials and their decryption keys;
            persistent mount credentials and forwarding configuration; application settings;
            an archive of host configuration; package/service/network/disk inventory; plan notes.
            Users' .ssh configuration is included separately for account/identity review.
            It is configuration recovery after reinstalling Linux or restoring a machine image.
            It does not reinstall Linux, recreate disks or back up Docker volumes automatically.

            1. Install Linux and LMS CE (the same or a newer LMS version) on the replacement.
               Keep managed services and scheduled backups off during recovery. Reconnect the
               backup storage. Use restic and your recovery password to restore the chosen
               snapshot to a PRIVATE alternate directory. If LMS is unavailable, run:
                   restic --repo /path/to/repository snapshots
                   restic --repo /path/to/repository restore SNAPSHOT_ID --target /private/recovery
               Restic asks for the password privately. For password-free storage, use --insecure-no-password with restic 0.17 or newer. Never put passwords in chat or command arguments.

            2. Find this RECOVERY.txt in the restored snapshot and validate its bundle:
                   python3 recover-lms-config.py --bundle /path/to/this/bundle
               Missing manifests indicate an older, incomplete backup. Missing files, changed
               checksums, invalid protection keys or an invalid database stop recovery.

            3. Prepare a NEW review directory using the replacement installation's actual paths:
                   python3 recover-lms-config.py --bundle /path/to/this/bundle \
                     --prepare /private/recovery-plan \
                     --data-root /var/lib/linuxmadesane/ce \
                     --app-root /opt/linuxmadesane/ce/current \
                     --keys-root /var/lib/linuxmadesane/ce/protection-keys
               Those are standard CE paths. Use your installation's configured paths if different.
               For application directories, use the real release directory behind the current
               symlink (readlink -f /opt/linuxmadesane/ce/current). Activation rejects symlink targets.
               Custom external stores require explicit --map STORE_ID=/replacement/path.
               If the database filename changed, add --database-file-name linuxmadesane.db
               (or the filename configured by the replacement installation).
               The tool creates root/ and restore-plan.json; it changes NO live files or services.

            4. Review restore-plan.json, host-inventory.json and host-configuration.tar.
               Map old disk UUIDs, interfaces, mount paths, hostnames/IPs and Caddy upstreams.
               Preserve the replacement's Linux machine identity and installer-generated service
               environment. Review application settings for old paths and environment overrides.
               Do not copy /etc wholesale or blindly restore old users, passwords or networking.
               Host archives include secret material and must remain private.
               user-ssh-for-review contains account SSH configuration. Match accounts and home
               directories on the replacement; retain restrictive .ssh permissions. Do not
               automatically copy another user's private keys or SSH identity into a new account.

            5. Stop LMS before replacing its database, protection keys and persistent files.
               Keep a private rollback copy of the replacement's current files. Copy reviewed
               root/ files using the included tool after reviewing the unchanged preparation tree:
                   sudo systemctl stop linux-made-sane.service
                   sudo python3 recover-lms-config.py --apply-prepared /private/recovery-plan --confirm-replace
               For a mounted replacement root while booted into rescue Linux, additionally use
               --target-root /mnt/replacement. The tool uses the target service account UID/GID,
               keeps a private rollback copy, rejects symlinks and changed preparation files,
               restores database and keys together, and never starts LMS or Linux services.
               Installer-generated service environment and old /etc files still require review.
               Do not merge old protection keys with unrelated current keys or keep SQLite WAL/SHM
               files from a different database. Database and protection keys are a matched set.

            6. Recreate packages and services with the existing LMS management tools. Regenerate
               schedules, SSH forwards and mounts only after reviewing paths, users and credentials.
               Validate SSH and Caddy configuration before enabling them. Review Edge Gateway DNS
               and public routes for the replacement host before publishing access.

            7. Start LMS and verify login, credential decryption, hosts, mounts, forwards and
               service configuration. Run a test backup and restore before enabling schedules.
               If validation fails, keep services stopped and restore the rollback copy.
            """, token);
    }
}
