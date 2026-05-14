using System.Text;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class SshfsRemoteMountService(
    LinuxMadeSaneDbContext dbContext,
    ILinuxCommandRunner commandRunner,
    IManagedHostStore managedHostStore,
    ISecretStore secretStore,
    ShareMountStorageSettings storageSettings) : ISshfsMountService
{
    private const string FstabFileSystemType = "fuse.sshfs";

    public async Task<IReadOnlyList<SshfsMountHostCandidate>> ListHostCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var hosts = await managedHostStore.ListAsync(cancellationToken);
        return hosts
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(BuildHostCandidate)
            .ToArray();
    }

    public async Task<IReadOnlyList<ManagedSshfsMount>> ListManagedMountsAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entities = await dbContext.SshfsMounts
            .AsNoTracking()
            .OrderBy(mount => mount.HostDisplayName)
            .ThenBy(mount => mount.RemotePath)
            .ToListAsync(cancellationToken);

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        return entities
            .Select(entity =>
            {
                var remoteSource = BuildRemoteSourcePath(entity.UserName, entity.HostAddress, entity.RemotePath);
                var isMounted = currentMounts.Any(mount =>
                    mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase) ||
                    mount.SourcePath.Equals(remoteSource, StringComparison.OrdinalIgnoreCase));

                return new ManagedSshfsMount(
                    entity.Id,
                    entity.HostId,
                    entity.HostDisplayName,
                    entity.HostAddress,
                    entity.Port,
                    entity.UserName,
                    entity.RemotePath,
                    entity.LocalMountPath,
                    isMounted,
                    entity.CreatedAtUtc,
                    entity.LastMountedAtUtc,
                    isMounted
                        ? "Mounted on this LMS server."
                        : "Saved as a permanent LMS SSHFS mount, but not currently mounted.");
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CurrentSystemMount>> ListCurrentMountsAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entities = await dbContext.SshfsMounts
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var managedSources = entities
            .Select(entity => BuildRemoteSourcePath(entity.UserName, entity.HostAddress, entity.RemotePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managedLocalPaths = entities
            .Select(entity => entity.LocalMountPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (await ReadCurrentMountsAsync(cancellationToken))
            .Where(IsSshfsMount)
            .Select(mount => mount with
            {
                IsManagedByLms = managedLocalPaths.Contains(mount.LocalMountPath) ||
                                 managedSources.Contains(mount.SourcePath)
            })
            .OrderByDescending(mount => mount.IsManagedByLms)
            .ThenBy(mount => mount.LocalMountPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<SshfsMountResult> CreateMountAsync(
        SshfsMountRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var host = await managedHostStore.GetAsync(request.HostId, cancellationToken)
            ?? throw new InvalidOperationException("The selected SSH host no longer exists.");
        var candidate = BuildHostCandidate(host);
        if (!candidate.CanMountWithSshfs)
        {
            throw new InvalidOperationException(candidate.StatusMessage);
        }

        var remotePath = NormalizeRemotePath(request.RemotePath);
        var localMountPath = NormalizeLocalMountPath(request.LocalMountPath);
        var remoteSourcePath = BuildRemoteSourcePath(host.Username, host.Hostname, remotePath);

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        if (currentMounts.Any(mount => mount.LocalMountPath.Equals(localMountPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"`{localMountPath}` is already mounted on this LMS server.");
        }

        if (request.PersistOnServer &&
            await dbContext.SshfsMounts.AnyAsync(mount => mount.LocalMountPath == localMountPath, cancellationToken))
        {
            throw new InvalidOperationException($"A permanent LMS SSHFS mount already manages `{localMountPath}`.");
        }

        var managedMountId = request.PersistOnServer ? Guid.NewGuid() : (Guid?)null;
        var identityFilePath = request.PersistOnServer
            ? BuildPersistentIdentityFilePath(managedMountId!.Value)
            : BuildTemporaryIdentityFilePath();

        try
        {
            await WriteIdentityFileAsync(host, identityFilePath, cancellationToken);
            await EnsureFuseAllowOtherAsync(cancellationToken);

            await RunRequiredCommandAsync(
                "mkdir",
                ["-p", localMountPath],
                $"Create LMS SSHFS mount path {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            await RunRequiredCommandAsync(
                "sshfs",
                BuildSshfsMountArguments(remoteSourcePath, localMountPath, host.Port, identityFilePath),
                $"Mount {remoteSourcePath} on {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            if (request.PersistOnServer)
            {
                await WriteOrUpdateFstabAsync(
                    managedMountId!.Value,
                    remoteSourcePath,
                    localMountPath,
                    BuildPersistentMountOptions(host.Port, identityFilePath),
                    cancellationToken);

                dbContext.SshfsMounts.Add(new SshfsMountEntity
                {
                    Id = managedMountId.Value,
                    HostId = host.Id,
                    HostDisplayName = host.Name,
                    HostAddress = host.Hostname,
                    Port = host.Port,
                    UserName = host.Username,
                    RemotePath = remotePath,
                    LocalMountPath = localMountPath,
                    IdentityFilePath = identityFilePath,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    LastMountedAtUtc = DateTimeOffset.UtcNow
                });

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return new SshfsMountResult(
                managedMountId,
                remoteSourcePath,
                localMountPath,
                request.PersistOnServer,
                request.PersistOnServer
                    ? $"Mounted {remoteSourcePath} and saved it as a permanent LMS SSHFS mount."
                    : $"Mounted {remoteSourcePath} temporarily on this LMS server.");
        }
        catch
        {
            if (!request.PersistOnServer || managedMountId.HasValue)
            {
                DeleteIfPresent(identityFilePath);
            }

            throw;
        }
    }

    public async Task DeleteManagedMountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entity = await dbContext.SshfsMounts.SingleOrDefaultAsync(mount => mount.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        if (currentMounts.Any(mount => mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase)))
        {
            await RunRequiredCommandAsync(
                "umount",
                [entity.LocalMountPath],
                $"Unmount {entity.LocalMountPath}",
                requiresSudo: true,
                cancellationToken);
        }

        await WriteFilteredFstabAsync(id, cancellationToken);
        DeleteIfPresent(entity.IdentityFilePath);

        dbContext.SshfsMounts.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task WriteIdentityFileAsync(
        ManagedHost host,
        string identityFilePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host.PrivateKeySecretReference))
        {
            throw new InvalidOperationException($"{host.Name} does not have a stored private key.");
        }

        if (!string.IsNullOrWhiteSpace(host.PrivateKeyPassphraseSecretReference))
        {
            throw new InvalidOperationException("SSHFS mounts need a stored private key that can be used non-interactively. Remove the key passphrase or use a dedicated mount key for this host.");
        }

        var privateKey = await secretStore.ResolveSecretAsync(host.PrivateKeySecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException($"{host.Name}'s stored private key could not be resolved.");
        }

        await File.WriteAllTextAsync(identityFilePath, privateKey.Trim() + Environment.NewLine, cancellationToken);
        SetUnixFileModeIfSupported(identityFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task EnsureFuseAllowOtherAsync(CancellationToken cancellationToken)
    {
        if (File.Exists("/etc/fuse.conf"))
        {
            var lines = await File.ReadAllLinesAsync("/etc/fuse.conf", cancellationToken);
            if (lines.Any(line => line.Trim().Equals("user_allow_other", StringComparison.Ordinal)))
            {
                return;
            }
        }

        const string command = "set -e; touch /etc/fuse.conf; " +
                               "if grep -Eq '^[[:space:]]*#?[[:space:]]*user_allow_other[[:space:]]*$' /etc/fuse.conf; " +
                               "then sed -i 's/^[[:space:]]*#[[:space:]]*user_allow_other[[:space:]]*$/user_allow_other/' /etc/fuse.conf; " +
                               "else printf '\\nuser_allow_other\\n' >> /etc/fuse.conf; fi";

        await RunRequiredCommandAsync(
            "sh",
            ["-c", command],
            "Enable FUSE allow_other for LMS SSHFS mounts",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task<IReadOnlyList<CurrentSystemMount>> ReadCurrentMountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists("/proc/mounts"))
        {
            return Array.Empty<CurrentSystemMount>();
        }

        var lines = await File.ReadAllLinesAsync("/proc/mounts", cancellationToken);
        return lines
            .Select(SambaRemoteMountService.ParseCurrentMount)
            .Where(mount => mount is not null)
            .Select(mount => mount!)
            .ToArray();
    }

    private async Task WriteOrUpdateFstabAsync(
        Guid mountId,
        string remoteSourcePath,
        string localMountPath,
        IReadOnlyList<string> mountOptions,
        CancellationToken cancellationToken)
    {
        var marker = BuildFstabMarker(mountId);
        var existingLines = await ReadFstabLinesAsync(cancellationToken);
        var filteredLines = existingLines
            .Where(line => !line.Contains(marker, StringComparison.Ordinal))
            .ToList();

        filteredLines.Add($"{EscapeFstabField(remoteSourcePath)} {EscapeFstabField(localMountPath)} {FstabFileSystemType} {string.Join(",", mountOptions)} 0 0 {marker}");
        await WriteFstabLinesAsync(filteredLines, cancellationToken);
    }

    private async Task WriteFilteredFstabAsync(Guid mountId, CancellationToken cancellationToken)
    {
        var marker = BuildFstabMarker(mountId);
        var existingLines = await ReadFstabLinesAsync(cancellationToken);
        var filteredLines = existingLines
            .Where(line => !line.Contains(marker, StringComparison.Ordinal))
            .ToArray();

        await WriteFstabLinesAsync(filteredLines, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ReadFstabLinesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists("/etc/fstab"))
        {
            return Array.Empty<string>();
        }

        return await File.ReadAllLinesAsync("/etc/fstab", cancellationToken);
    }

    private async Task WriteFstabLinesAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        var stagedPath = Path.Combine(storageSettings.StagingDirectory, "fstab.sshfs.lms");
        await File.WriteAllTextAsync(stagedPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
        SetUnixFileModeIfSupported(
            stagedPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        if (string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(stagedPath, "/etc/fstab", overwrite: true);
            return;
        }

        await RunRequiredCommandAsync(
            "cp",
            [stagedPath, "/etc/fstab"],
            "Update /etc/fstab for LMS SSHFS mounts",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(45), description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"{description} failed with exit code {result.ExitCode}."
            : $"{description} failed: {message}");
    }

    private void EnsureStorageDirectories()
    {
        Directory.CreateDirectory(storageSettings.RootDirectory);
        Directory.CreateDirectory(storageSettings.RuntimeDirectory);
        Directory.CreateDirectory(storageSettings.StagingDirectory);
        Directory.CreateDirectory(BuildIdentityDirectory());
    }

    private string BuildPersistentIdentityFilePath(Guid mountId) =>
        Path.Combine(BuildIdentityDirectory(), $"{mountId:N}.key");

    private string BuildTemporaryIdentityFilePath() =>
        Path.Combine(storageSettings.RuntimeDirectory, $"{Guid.NewGuid():N}.sshfs.key");

    private string BuildIdentityDirectory() =>
        Path.Combine(storageSettings.RootDirectory, "sshfs-keys");

    private static SshfsMountHostCandidate BuildHostCandidate(ManagedHost host)
    {
        var hasPrivateKeyAuthentication = host.PrimaryAuthenticationType == AuthenticationType.PrivateKey ||
                                          host.FallbackAuthenticationType == AuthenticationType.PrivateKey;
        var hasStoredPrivateKey = !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference);
        var hasPrivateKeyPassphrase = !string.IsNullOrWhiteSpace(host.PrivateKeyPassphraseSecretReference);
        var canMount = hasPrivateKeyAuthentication &&
                       hasStoredPrivateKey &&
                       !hasPrivateKeyPassphrase &&
                       !string.IsNullOrWhiteSpace(host.Username) &&
                       !string.IsNullOrWhiteSpace(host.Hostname);

        var status = canMount
            ? "Ready for SSHFS. This host has stored key-based SSH authentication."
            : BuildHostCandidateFailure(host, hasPrivateKeyAuthentication, hasStoredPrivateKey, hasPrivateKeyPassphrase);

        return new SshfsMountHostCandidate(
            host.Id,
            host.Name,
            host.Hostname,
            host.Port <= 0 ? 22 : host.Port,
            host.Username,
            string.IsNullOrWhiteSpace(host.DefaultWorkingDirectory) ? "/" : host.DefaultWorkingDirectory,
            host.PrimaryAuthenticationType,
            host.FallbackAuthenticationType,
            hasStoredPrivateKey,
            hasPrivateKeyPassphrase,
            canMount,
            status);
    }

    private static string BuildHostCandidateFailure(
        ManagedHost host,
        bool hasPrivateKeyAuthentication,
        bool hasStoredPrivateKey,
        bool hasPrivateKeyPassphrase)
    {
        if (string.IsNullOrWhiteSpace(host.Hostname) || string.IsNullOrWhiteSpace(host.Username))
        {
            return "Host and username are required before LMS can create an SSHFS mount.";
        }

        if (!hasPrivateKeyAuthentication)
        {
            return "SSHFS mounts require the registered host to use public key authentication.";
        }

        if (!hasStoredPrivateKey)
        {
            return "SSHFS mounts require a stored private key on the registered host.";
        }

        if (hasPrivateKeyPassphrase)
        {
            return "SSHFS automated mounts need a non-interactive key. Use a dedicated unencrypted mount key for this host.";
        }

        return "This host is not ready for SSHFS.";
    }

    private static IReadOnlyList<string> BuildSshfsMountArguments(
        string remoteSourcePath,
        string localMountPath,
        int port,
        string identityFilePath) =>
        [
            remoteSourcePath,
            localMountPath,
            "-p",
            port.ToString(),
            "-o",
            string.Join(",", BuildRuntimeMountOptions(port, identityFilePath))
        ];

    private static IReadOnlyList<string> BuildRuntimeMountOptions(int port, string identityFilePath) =>
        [
            $"IdentityFile={identityFilePath}",
            "IdentitiesOnly=yes",
            "StrictHostKeyChecking=accept-new",
            "reconnect",
            "ServerAliveInterval=15",
            "ServerAliveCountMax=3",
            "allow_other",
            $"port={port}"
        ];

    private static IReadOnlyList<string> BuildPersistentMountOptions(int port, string identityFilePath)
    {
        var options = BuildRuntimeMountOptions(port, identityFilePath).ToList();
        options.Add("_netdev");
        options.Add("nofail");
        options.Add("x-systemd.automount");
        return options;
    }

    private static bool IsSshfsMount(CurrentSystemMount mount) =>
        mount.FileSystemType.Equals("sshfs", StringComparison.OrdinalIgnoreCase) ||
        mount.FileSystemType.Equals("fuse.sshfs", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRemotePath(string remotePath)
    {
        var trimmed = string.IsNullOrWhiteSpace(remotePath) ? "/" : remotePath.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote SSH path must be absolute, for example `/srv/data`.");
        }

        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('\t'))
        {
            throw new InvalidOperationException("The remote SSH path contains unsupported whitespace.");
        }

        return trimmed;
    }

    private static string NormalizeLocalMountPath(string localMountPath)
    {
        var trimmed = localMountPath.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            throw new InvalidOperationException("The LMS mount path must be an absolute Linux path.");
        }

        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('\t'))
        {
            throw new InvalidOperationException("The LMS mount path contains unsupported whitespace.");
        }

        return trimmed;
    }

    private static string BuildRemoteSourcePath(string userName, string hostname, string remotePath) =>
        $"{userName.Trim()}@{hostname.Trim()}:{NormalizeRemotePath(remotePath)}";

    private static string BuildFstabMarker(Guid mountId) =>
        $"# lms-sshfs-mount:{mountId:N}";

    private static string EscapeFstabField(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                ' ' => "\\040",
                '\t' => "\\011",
                '\\' => "\\134",
                _ => character
            });
        }

        return builder.ToString();
    }

    private static void DeleteIfPresent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void SetUnixFileModeIfSupported(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
