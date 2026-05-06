using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class SambaRemoteMountService(
    LinuxMadeSaneDbContext dbContext,
    ILinuxCommandRunner commandRunner,
    ShareMountStorageSettings storageSettings)
{
    private static readonly HashSet<string> NetworkFileSystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cifs",
        "smb3",
        "nfs",
        "nfs4",
        "sshfs",
        "fuse.sshfs",
        "ceph",
        "davfs",
        "davfs2",
        "glusterfs"
    };

    public async Task<IReadOnlyList<ManagedRemoteShareMount>> ListManagedMountsAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entities = await dbContext.RemoteShareMounts
            .AsNoTracking()
            .OrderBy(mount => mount.RemoteHost)
            .ThenBy(mount => mount.ShareName)
            .ToListAsync(cancellationToken);

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);

        return entities
            .Select(entity =>
            {
                var isMounted = currentMounts.Any(mount =>
                    mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase) ||
                    mount.SourcePath.Equals(BuildRemoteUncPath(entity.RemoteHost, entity.ShareName), StringComparison.OrdinalIgnoreCase));

                return new ManagedRemoteShareMount(
                    entity.Id,
                    entity.RemoteHost,
                    entity.RemoteAddress,
                    entity.ShareName,
                    entity.LocalMountPath,
                    entity.UserName,
                    entity.Domain,
                    !string.IsNullOrWhiteSpace(entity.CredentialFilePath) && File.Exists(entity.CredentialFilePath),
                    isMounted,
                    entity.CreatedAtUtc,
                    entity.LastMountedAtUtc,
                    isMounted
                        ? "Mounted on this LMS server."
                        : "Saved as a permanent LMS mount, but not currently mounted.");
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CurrentSystemMount>> ListCurrentMountsAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var managedEntities = await dbContext.RemoteShareMounts
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);

        return currentMounts
            .Select(mount => mount with
            {
                IsManagedByLms = managedEntities.Any(entity =>
                    mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase) ||
                    mount.SourcePath.Equals(BuildRemoteUncPath(entity.RemoteHost, entity.ShareName), StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(mount => mount.IsManagedByLms)
            .ThenByDescending(mount => mount.IsNetworkMount)
            .ThenBy(mount => mount.LocalMountPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RemoteShareMountResult> CreateRemoteShareMountAsync(
        RemoteShareMountRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var remoteHost = request.RemoteHost.Trim();
        var shareName = request.ShareName.Trim();
        var localMountPath = request.LocalMountPath.Trim();

        if (remoteHost.Length == 0 || shareName.Length == 0)
        {
            throw new InvalidOperationException("Both the remote machine and share name are required.");
        }

        if (!Path.IsPathRooted(localMountPath))
        {
            throw new InvalidOperationException("The LMS mount path must be an absolute Linux path.");
        }

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        if (currentMounts.Any(mount => mount.LocalMountPath.Equals(localMountPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"`{localMountPath}` is already mounted on this LMS server.");
        }

        if (request.PersistOnServer &&
            await dbContext.RemoteShareMounts.AnyAsync(mount => mount.LocalMountPath == localMountPath, cancellationToken))
        {
            throw new InvalidOperationException($"A permanent LMS mount already manages `{localMountPath}`.");
        }

        var managedMountId = request.PersistOnServer ? Guid.NewGuid() : (Guid?)null;
        var credentialFilePath = request.PersistOnServer
            ? BuildPersistentCredentialFilePath(managedMountId!.Value)
            : null;
        var temporaryCredentialFilePath = request.PersistOnServer
            ? null
            : BuildTemporaryCredentialFilePath();

        var mountCredentialFilePath = credentialFilePath ?? temporaryCredentialFilePath;
        var hasCredentials = !string.IsNullOrWhiteSpace(request.UserName) ||
                             !string.IsNullOrWhiteSpace(request.Password) ||
                             !string.IsNullOrWhiteSpace(request.Domain);

        if (hasCredentials)
        {
            await WriteCredentialsFileAsync(
                mountCredentialFilePath!,
                request.UserName,
                request.Password,
                request.Domain,
                cancellationToken);
        }

        var remoteUncPath = BuildRemoteUncPath(remoteHost, shareName);
        var temporaryMountMarker = request.PersistOnServer
            ? null
            : new TemporaryRemoteMountMarker(Guid.NewGuid(), remoteUncPath, localMountPath, DateTimeOffset.UtcNow);

        if (temporaryMountMarker is not null)
        {
            await WriteTemporaryMountMarkerAsync(temporaryMountMarker, cancellationToken);
        }

        try
        {
            await RunRequiredCommandAsync(
                "mkdir",
                ["-p", localMountPath],
                $"Create LMS mount path {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            var mountOptions = BuildMountOptions(hasCredentials, mountCredentialFilePath);

            await RunRequiredCommandAsync(
                "mount",
                ["-t", "cifs", remoteUncPath, localMountPath, "-o", string.Join(",", mountOptions)],
                $"Mount {remoteUncPath} on {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            if (request.PersistOnServer)
            {
                await WriteOrUpdateFstabAsync(
                    managedMountId!.Value,
                    remoteUncPath,
                    localMountPath,
                    BuildPersistentMountOptions(hasCredentials, credentialFilePath),
                    cancellationToken);

                dbContext.RemoteShareMounts.Add(new RemoteShareMountEntity
                {
                    Id = managedMountId.Value,
                    RemoteHost = remoteHost,
                    RemoteAddress = NullIfWhiteSpace(request.RemoteAddress),
                    ShareName = shareName,
                    LocalMountPath = localMountPath,
                    UserName = NullIfWhiteSpace(request.UserName),
                    Domain = NullIfWhiteSpace(request.Domain),
                    CredentialFilePath = credentialFilePath,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    LastMountedAtUtc = DateTimeOffset.UtcNow
                });

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return new RemoteShareMountResult(
                managedMountId,
                remoteUncPath,
                localMountPath,
                request.PersistOnServer,
                request.PersistOnServer
                    ? $"Mounted {remoteUncPath} and saved it as a permanent LMS mount."
                    : $"Mounted {remoteUncPath} temporarily on this LMS server.");
        }
        catch
        {
            if (temporaryMountMarker is not null)
            {
                DeleteIfPresent(BuildTemporaryMountMarkerPath(temporaryMountMarker.Id));
            }

            if (request.PersistOnServer && !string.IsNullOrWhiteSpace(credentialFilePath))
            {
                DeleteIfPresent(credentialFilePath);
            }

            throw;
        }
        finally
        {
            if (!request.PersistOnServer && !string.IsNullOrWhiteSpace(temporaryCredentialFilePath))
            {
                DeleteIfPresent(temporaryCredentialFilePath);
            }
        }
    }

    public async Task<RemoteShareMountResult> UpdateManagedRemoteMountAsync(
        Guid id,
        RemoteShareMountRequest request,
        bool keepSavedPassword,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entity = await dbContext.RemoteShareMounts.SingleOrDefaultAsync(mount => mount.Id == id, cancellationToken);
        if (entity is null)
        {
            throw new InvalidOperationException("The managed LMS mount no longer exists.");
        }

        var remoteHost = request.RemoteHost.Trim();
        var shareName = request.ShareName.Trim();
        var localMountPath = request.LocalMountPath.Trim();

        if (remoteHost.Length == 0 || shareName.Length == 0)
        {
            throw new InvalidOperationException("Both the remote machine and share name are required.");
        }

        if (!Path.IsPathRooted(localMountPath))
        {
            throw new InvalidOperationException("The LMS mount path must be an absolute Linux path.");
        }

        var oldRemoteUncPath = BuildRemoteUncPath(entity.RemoteHost, entity.ShareName);
        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        var currentMount = currentMounts.FirstOrDefault(mount =>
            mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase) ||
            mount.SourcePath.Equals(oldRemoteUncPath, StringComparison.OrdinalIgnoreCase));

        if (currentMounts.Any(mount =>
                !mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase) &&
                mount.LocalMountPath.Equals(localMountPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"`{localMountPath}` is already mounted on this LMS server.");
        }

        if (await dbContext.RemoteShareMounts.AnyAsync(
                mount => mount.Id != id && mount.LocalMountPath == localMountPath,
                cancellationToken))
        {
            throw new InvalidOperationException($"A permanent LMS mount already manages `{localMountPath}`.");
        }

        var credentialFileSnapshot = await CaptureCredentialFileSnapshotAsync(entity.CredentialFilePath, cancellationToken);
        var existingCredentials = await ReadCredentialFileAsync(entity.CredentialFilePath, cancellationToken);
        var password = !string.IsNullOrWhiteSpace(request.Password)
            ? request.Password.Trim()
            : keepSavedPassword
                ? existingCredentials?.Password
                : null;
        var hasCredentials = !string.IsNullOrWhiteSpace(request.UserName) ||
                             !string.IsNullOrWhiteSpace(password) ||
                             !string.IsNullOrWhiteSpace(request.Domain);
        var credentialFilePath = hasCredentials
            ? entity.CredentialFilePath ?? BuildPersistentCredentialFilePath(id)
            : null;
        var newRemoteUncPath = BuildRemoteUncPath(remoteHost, shareName);
        var wasMounted = currentMount is not null;

        try
        {
            if (currentMount is not null)
            {
                await RunRequiredCommandAsync(
                    "umount",
                    [currentMount.LocalMountPath],
                    $"Unmount {currentMount.LocalMountPath}",
                    requiresSudo: true,
                    cancellationToken);
            }

            await RunRequiredCommandAsync(
                "mkdir",
                ["-p", localMountPath],
                $"Create LMS mount path {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            if (hasCredentials)
            {
                await WriteCredentialsFileAsync(
                    credentialFilePath!,
                    request.UserName,
                    password,
                    request.Domain,
                    cancellationToken);
            }
            else
            {
                DeleteIfPresent(entity.CredentialFilePath);
            }

            await RunRequiredCommandAsync(
                "mount",
                ["-t", "cifs", newRemoteUncPath, localMountPath, "-o", string.Join(",", BuildMountOptions(hasCredentials, credentialFilePath))],
                $"Mount {newRemoteUncPath} on {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            await WriteOrUpdateFstabAsync(
                id,
                newRemoteUncPath,
                localMountPath,
                BuildPersistentMountOptions(hasCredentials, credentialFilePath),
                cancellationToken);

            entity.RemoteHost = remoteHost;
            entity.RemoteAddress = NullIfWhiteSpace(request.RemoteAddress);
            entity.ShareName = shareName;
            entity.LocalMountPath = localMountPath;
            entity.UserName = NullIfWhiteSpace(request.UserName);
            entity.Domain = NullIfWhiteSpace(request.Domain);
            entity.CredentialFilePath = credentialFilePath;
            entity.LastMountedAtUtc = DateTimeOffset.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken);

            return new RemoteShareMountResult(
                id,
                newRemoteUncPath,
                localMountPath,
                Persisted: true,
                $"Updated {newRemoteUncPath} and remounted it on this LMS server.");
        }
        catch
        {
            await RestoreCredentialFileSnapshotAsync(credentialFileSnapshot, cancellationToken);

            if (wasMounted)
            {
                var rollbackHasCredentials = credentialFileSnapshot.Exists;
                await RunCleanupCommandAsync(
                    "mount",
                    [
                        "-t",
                        "cifs",
                        oldRemoteUncPath,
                        entity.LocalMountPath,
                        "-o",
                        string.Join(",", BuildMountOptions(rollbackHasCredentials, rollbackHasCredentials ? entity.CredentialFilePath : null))
                    ],
                    $"Restore {oldRemoteUncPath} on {entity.LocalMountPath}",
                    requiresSudo: true,
                    cancellationToken);
            }

            throw;
        }
    }

    public async Task CleanupTemporaryMountsAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var markers = await ReadTemporaryMountMarkersAsync(cancellationToken);
        foreach (var marker in markers)
        {
            await ReleaseTemporaryMountAsync(marker.LocalMountPath, cancellationToken);
        }
    }

    public async Task<bool> ReleaseTemporaryMountAsync(
        string localMountPath,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var marker = await FindTemporaryMountMarkerAsync(localMountPath, cancellationToken);
        if (marker is null)
        {
            return false;
        }

        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        var currentMount = currentMounts.FirstOrDefault(mount =>
            mount.LocalMountPath.Equals(marker.LocalMountPath, StringComparison.OrdinalIgnoreCase) ||
            mount.SourcePath.Equals(marker.RemoteUncPath, StringComparison.OrdinalIgnoreCase));

        if (currentMount is not null)
        {
            await RunCleanupCommandAsync(
                "umount",
                [currentMount.LocalMountPath],
                $"Unmount temporary LMS mount {currentMount.LocalMountPath}",
                requiresSudo: true,
                cancellationToken);
        }

        DeleteIfPresent(BuildTemporaryMountMarkerPath(marker.Id));
        TryDeleteDirectoryIfEmpty(marker.LocalMountPath);
        return true;
    }

    public async Task<bool> DisconnectCurrentRemoteMountAsync(
        string localMountPath,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        if (await ReleaseTemporaryMountAsync(localMountPath, cancellationToken))
        {
            return true;
        }

        var normalizedPath = localMountPath.Trim();
        var currentMount = (await ReadCurrentMountsAsync(cancellationToken))
            .FirstOrDefault(mount => mount.LocalMountPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (currentMount is null)
        {
            return false;
        }

        await RunRequiredCommandAsync(
            "umount",
            [currentMount.LocalMountPath],
            $"Unmount {currentMount.LocalMountPath}",
            requiresSudo: true,
            cancellationToken);

        TryDeleteDirectoryIfEmpty(currentMount.LocalMountPath);
        return true;
    }

    public async Task DeleteManagedRemoteMountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entity = await dbContext.RemoteShareMounts.SingleOrDefaultAsync(mount => mount.Id == id, cancellationToken);
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
        DeleteIfPresent(entity.CredentialFilePath);

        dbContext.RemoteShareMounts.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
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
            .Select(ParseCurrentMount)
            .Where(mount => mount is not null)
            .Select(mount => mount!)
            .ToArray();
    }

    private async Task WriteOrUpdateFstabAsync(
        Guid mountId,
        string remoteUncPath,
        string localMountPath,
        IReadOnlyList<string> mountOptions,
        CancellationToken cancellationToken)
    {
        var marker = BuildFstabMarker(mountId);
        var existingLines = await ReadFstabLinesAsync(cancellationToken);
        var filteredLines = existingLines
            .Where(line => !line.Contains(marker, StringComparison.Ordinal))
            .ToList();

        filteredLines.Add($"{remoteUncPath} {localMountPath} cifs {string.Join(",", mountOptions)} 0 0 {marker}");
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
        var stagedPath = Path.Combine(storageSettings.StagingDirectory, "fstab.lms");
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
            "Update /etc/fstab for LMS remote share mounts",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task WriteCredentialsFileAsync(
        string path,
        string? userName,
        string? password,
        string? domain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(userName))
        {
            lines.Add($"username={userName.Trim()}");
        }

        lines.Add($"password={password?.Trim() ?? string.Empty}");

        if (!string.IsNullOrWhiteSpace(domain))
        {
            lines.Add($"domain={domain.Trim()}");
        }

        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
        SetUnixFileModeIfSupported(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<CredentialFileSnapshot> CaptureCredentialFileSnapshotAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new CredentialFileSnapshot(path, Exists: false, Content: null);
        }

        return new CredentialFileSnapshot(path, Exists: true, await File.ReadAllTextAsync(path, cancellationToken));
    }

    private async Task RestoreCredentialFileSnapshotAsync(
        CredentialFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path))
        {
            return;
        }

        if (!snapshot.Exists)
        {
            DeleteIfPresent(snapshot.Path);
            return;
        }

        await File.WriteAllTextAsync(snapshot.Path, snapshot.Content ?? string.Empty, cancellationToken);
        SetUnixFileModeIfSupported(snapshot.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<StoredMountCredentials?> ReadCredentialFileAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        string? userName = null;
        string? password = null;
        string? domain = null;

        foreach (var line in lines)
        {
            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (key.Equals("username", StringComparison.OrdinalIgnoreCase))
            {
                userName = value;
                continue;
            }

            if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                password = value;
                continue;
            }

            if (key.Equals("domain", StringComparison.OrdinalIgnoreCase))
            {
                domain = value;
            }
        }

        return new StoredMountCredentials(userName, password, domain);
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(30), description),
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

    private async Task RunCleanupCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunRequiredCommandAsync(fileName, arguments, description, requiresSudo, cancellationToken);
        }
        catch
        {
            // Cleanup must not block app startup or leave marker files behind forever.
        }
    }

    private void EnsureStorageDirectories()
    {
        Directory.CreateDirectory(storageSettings.RootDirectory);
        Directory.CreateDirectory(storageSettings.CredentialsDirectory);
        Directory.CreateDirectory(storageSettings.RuntimeDirectory);
        Directory.CreateDirectory(storageSettings.TemporaryMountDirectory);
        Directory.CreateDirectory(storageSettings.StagingDirectory);
    }

    private async Task WriteTemporaryMountMarkerAsync(
        TemporaryRemoteMountMarker marker,
        CancellationToken cancellationToken)
    {
        var markerPath = BuildTemporaryMountMarkerPath(marker.Id);
        var json = JsonSerializer.Serialize(marker);
        await File.WriteAllTextAsync(markerPath, json, cancellationToken);
        SetUnixFileModeIfSupported(markerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<IReadOnlyList<TemporaryRemoteMountMarker>> ReadTemporaryMountMarkersAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(storageSettings.TemporaryMountDirectory))
        {
            return [];
        }

        var markers = new List<TemporaryRemoteMountMarker>();
        foreach (var markerPath in Directory.EnumerateFiles(storageSettings.TemporaryMountDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(markerPath, cancellationToken);
                var marker = JsonSerializer.Deserialize<TemporaryRemoteMountMarker>(json);
                if (marker is not null)
                {
                    markers.Add(marker);
                }
            }
            catch
            {
                DeleteIfPresent(markerPath);
            }
        }

        return markers;
    }

    private async Task<TemporaryRemoteMountMarker?> FindTemporaryMountMarkerAsync(
        string localMountPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = localMountPath.Trim();
        return (await ReadTemporaryMountMarkersAsync(cancellationToken))
            .FirstOrDefault(marker => marker.LocalMountPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildMountOptions(bool hasCredentials, string? credentialFilePath)
    {
        var options = new List<string>
        {
            "vers=3.1.1",
            "iocharset=utf8",
            "_netdev",
            "serverino"
        };

        if (hasCredentials)
        {
            options.Add($"credentials={credentialFilePath}");
        }
        else
        {
            options.Add("guest");
        }

        return options;
    }

    private static IReadOnlyList<string> BuildPersistentMountOptions(bool hasCredentials, string? credentialFilePath)
    {
        var options = BuildMountOptions(hasCredentials, credentialFilePath).ToList();
        options.Add("nofail");
        options.Add("x-systemd.automount");
        return options;
    }

    private static string BuildRemoteUncPath(string remoteHost, string shareName) =>
        $"//{remoteHost.Trim()}/{shareName.Trim()}";

    private string BuildPersistentCredentialFilePath(Guid mountId) =>
        Path.Combine(storageSettings.CredentialsDirectory, $"{mountId:N}.cred");

    private string BuildTemporaryCredentialFilePath() =>
        Path.Combine(storageSettings.RuntimeDirectory, $"{Guid.NewGuid():N}.cred");

    private string BuildTemporaryMountMarkerPath(Guid markerId) =>
        Path.Combine(storageSettings.TemporaryMountDirectory, $"{markerId:N}.json");

    private static string BuildFstabMarker(Guid mountId) =>
        $"# lms-remote-mount:{mountId:N}";

    internal static CurrentSystemMount? ParseCurrentMount(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        var sourcePath = DecodeMountField(parts[0]);
        var localMountPath = DecodeMountField(parts[1]);
        var fileSystemType = DecodeMountField(parts[2]);
        var mountOptions = DecodeMountField(parts[3]);

        return new CurrentSystemMount(
            sourcePath,
            localMountPath,
            fileSystemType,
            mountOptions,
            IsReadOnlyMount(mountOptions),
            IsNetworkMount(fileSystemType, sourcePath),
            IsManagedByLms: false);
    }

    private static bool IsReadOnlyMount(string mountOptions) =>
        mountOptions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(option => option.Equals("ro", StringComparison.OrdinalIgnoreCase));

    private static bool IsNetworkMount(string fileSystemType, string sourcePath) =>
        NetworkFileSystemTypes.Contains(fileSystemType) ||
        sourcePath.StartsWith("//", StringComparison.Ordinal) ||
        !Path.IsPathRooted(sourcePath) &&
        sourcePath.Contains(':', StringComparison.Ordinal);

    private static string DecodeMountField(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' &&
                index + 3 < value.Length &&
                IsOctalDigit(value[index + 1]) &&
                IsOctalDigit(value[index + 2]) &&
                IsOctalDigit(value[index + 3]))
            {
                var octalValue = Convert.ToInt32(value.Substring(index + 1, 3), 8);
                builder.Append((char)octalValue);
                index += 3;
                continue;
            }

            builder.Append(value[index]);
        }

        return builder.ToString();
    }

    private static bool IsOctalDigit(char value) =>
        value is >= '0' and <= '7';

    private static void DeleteIfPresent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetUnixFileModeIfSupported(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private sealed record TemporaryRemoteMountMarker(
        Guid Id,
        string RemoteUncPath,
        string LocalMountPath,
        DateTimeOffset CreatedAtUtc);

    private sealed record CredentialFileSnapshot(
        string? Path,
        bool Exists,
        string? Content);

    private sealed record StoredMountCredentials(
        string? UserName,
        string? Password,
        string? Domain);
}
