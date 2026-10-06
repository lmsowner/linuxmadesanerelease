// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using System.Security.Cryptography;
using Renci.SshNet.Common;
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
    ShareMountStorageSettings storageSettings,
    ManagedHostSshConnectionFactory? sshConnectionFactory = null) : ISshfsMountService
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

        if (request.PersistOnServer && !candidate.CanPersistWithSshfs)
            throw new InvalidOperationException("Persistent SSH mounts require a saved, non-interactive private key. Select a temporary mount to use username and password.");
        var usePassword = !request.PersistOnServer && candidate.HasStoredPassword &&
            (host.PrimaryAuthenticationType == AuthenticationType.Password || !candidate.CanPersistWithSshfs);
        byte[]? passwordInput = null;
        string? password = null;
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
            if (usePassword)
            {
                password = await secretStore.ResolveSecretAsync(host.PasswordSecretReference!, cancellationToken);
                if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("The saved SSH password is unavailable. Edit and test this host's credentials before mounting.");
                if (password.Contains('\n') || password.Contains('\r')) throw new InvalidOperationException("SSHFS password input does not support passwords containing line breaks.");
                passwordInput = Encoding.UTF8.GetBytes(password + "\n");
            }
            else await WriteIdentityFileAsync(host, identityFilePath, cancellationToken);
            await EnsureFuseAllowOtherAsync(cancellationToken);

            await RunRequiredCommandAsync(
                "mkdir",
                ["-p", localMountPath],
                $"Create LMS SSHFS mount path {localMountPath}",
                requiresSudo: true,
                cancellationToken);

            try
            {
            await RunRequiredCommandAsync(
                "sshfs",
                usePassword
                    ? BuildPasswordMountArguments(remoteSourcePath, localMountPath, host.Port)
                    : BuildSshfsMountArguments(remoteSourcePath, localMountPath, host.Port, identityFilePath),
                $"Mount {remoteSourcePath} on {localMountPath}",
                requiresSudo: true,
                cancellationToken,
                mountFailureContext: remoteSourcePath,
                mountDiagnosticRequest: usePassword ? null : BuildSftpDiagnosticRequest(host.Hostname, host.Username, host.Port, identityFilePath, remotePath),
                passwordInput: passwordInput, redactedPassword: password);
            }
            catch (InvalidOperationException exception) when (usePassword && sshConnectionFactory is not null &&
                (exception.Message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) ||
                 exception.Message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase) ||
                 exception.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)))
            {
                var diagnosis = await DiagnosePasswordMountFailureAsync(host, password!, remotePath, cancellationToken);
                throw new InvalidOperationException($"Could not mount {remoteSourcePath}. {diagnosis} Mount detail: {exception.Message}");
            }

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
        finally
        {
            if (passwordInput is not null) CryptographicOperations.ZeroMemory(passwordInput);
        }
    }

    public async Task<SshfsMountResult?> ReconnectManagedMountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureStorageDirectories();

        var entity = await dbContext.SshfsMounts.SingleOrDefaultAsync(mount => mount.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var remoteSourcePath = BuildRemoteSourcePath(entity.UserName, entity.HostAddress, entity.RemotePath);
        var currentMounts = await ReadCurrentMountsAsync(cancellationToken);
        if (currentMounts.Any(mount =>
                mount.LocalMountPath.Equals(entity.LocalMountPath, StringComparison.OrdinalIgnoreCase) ||
                mount.SourcePath.Equals(remoteSourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            return new SshfsMountResult(
                id,
                remoteSourcePath,
                entity.LocalMountPath,
                Persisted: true,
                $"{remoteSourcePath} is already mounted.");
        }

        await RunRequiredCommandAsync(
            "mkdir",
            ["-p", entity.LocalMountPath],
            $"Create LMS SSHFS mount path {entity.LocalMountPath}",
            requiresSudo: true,
            cancellationToken);

        var host = await managedHostStore.GetAsync(entity.HostId, cancellationToken)
            ?? throw new InvalidOperationException("The saved SSH mount's host no longer exists.");
        await WriteIdentityFileAsync(host, entity.IdentityFilePath, cancellationToken);

        await RunRequiredCommandAsync(
            "mount",
            ["-o", "BatchMode=yes,PasswordAuthentication=no,KbdInteractiveAuthentication=no,ConnectTimeout=10", entity.LocalMountPath],
            $"Reconnect LMS SSHFS mount {entity.LocalMountPath}",
            requiresSudo: true,
            cancellationToken,
            mountFailureContext: remoteSourcePath,
            mountDiagnosticRequest: BuildSftpDiagnosticRequest(entity.HostAddress, entity.UserName, entity.Port, entity.IdentityFilePath, entity.RemotePath));

        entity.LastMountedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new SshfsMountResult(
            id,
            remoteSourcePath,
            entity.LocalMountPath,
            Persisted: true,
            $"Reconnected {remoteSourcePath} on {entity.LocalMountPath}.");
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

    internal async Task<string> DiagnosePasswordMountFailureAsync(ManagedHost host, string password, string remotePath, CancellationToken token)
    {
        // SSHFS/OpenSSH already verified or rejected the server identity. Reuse its
        // root-owned known_hosts trust for the diagnostic; never send a password to
        // an identity that OpenSSH has not accepted.
        var lookup = host.Port is 22 or <= 0 ? host.Hostname : $"[{host.Hostname}]:{host.Port}";
        var known = await commandRunner.RunAsync(new("ssh-keygen", ["-F", lookup], true,
            TimeSpan.FromSeconds(5), "Read SSHFS trusted host identity for password diagnostics"), false, token);
        var keys = known.StandardOutput.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 3 && !fields[0].StartsWith('#') && !fields[0].StartsWith('@'))
            .Select(fields => fields[2]).ToHashSet(StringComparer.Ordinal);
        if (known.ExitCode != 0 || keys.Count == 0)
            return "SSH login could not be confirmed. The server identity is not present in this LMS host's SSHFS trusted-host file; verify the host key before retrying.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var client = sshConnectionFactory!.CreateSftpClient(host,
                new ManagedHostSshCredentials(host.Username, password, null, null, AuthenticationType.Password), TimeSpan.FromSeconds(10));
            client.OperationTimeout = TimeSpan.FromSeconds(10);
            client.HostKeyReceived += (_, args) => args.CanTrust = keys.Contains(Convert.ToBase64String(args.HostKey));
            await client.ConnectAsync(timeout.Token);
            await Task.Run(() => client.GetAttributes(remotePath), timeout.Token);
            return "Saved username/password login and SFTP access to the remote folder succeeded. Check the local FUSE/mount-point error or an intermittent connection failure.";
        }
        catch (SshAuthenticationException) { return "The SSH server rejected the saved username/password. Test the saved credentials and check that password authentication is allowed for this user on the remote server."; }
        catch (SftpPermissionDeniedException) { return "Password login succeeded, but the remote server denied access to this folder. Check its permissions and this user's SFTP restrictions."; }
        catch (SftpPathNotFoundException) { return "Password login succeeded, but this folder does not exist on the remote server. Check the remote path."; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return "The password/SFTP diagnostic timed out. Check connectivity and the remote SSH service; login could not be confirmed."; }
        catch (SshException) { return "The password/SFTP diagnostic could not complete. Verify the server identity and that its SFTP subsystem is enabled; login could not be confirmed."; }
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken,
        string? mountFailureContext = null,
        LinuxCommandRequest? mountDiagnosticRequest = null,
        byte[]? passwordInput = null, string? redactedPassword = null)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(45), description) { StandardInputBytes = passwordInput },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

        if (!string.IsNullOrEmpty(redactedPassword)) message = message.Replace(redactedPassword, "[redacted]", StringComparison.Ordinal);

        if (mountFailureContext is not null)
        {
            if (mountDiagnosticRequest is not null)
            {
                var diagnostic = await commandRunner.RunAsync(mountDiagnosticRequest, dryRun: false, cancellationToken);
                if (diagnostic.ExitCode == 0)
                {
                    throw new InvalidOperationException($"Could not mount {mountFailureContext}. " +
                        "SFTP login and access to the remote folder succeeded using the saved key. " +
                        "The SSHFS mount still failed. Check the mount detail below for a local FUSE, mount-point or intermittent connection problem. " +
                        $"Mount detail: {message[..Math.Min(message.Length, 800)]}");
                }

                message = diagnostic.StandardError + "\n" + message;
            }

            throw new InvalidOperationException(SshfsMountFailure.Describe(mountFailureContext, message, result.ExitCode, passwordInput is not null));
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"{description} failed with exit code {result.ExitCode}."
            : $"{description} failed: {message}");
    }

    private static LinuxCommandRequest BuildSftpDiagnosticRequest(
        string hostname, string username, int port, string identityFilePath, string remotePath) =>
        new("sftp",
            ["-v", "-b", "-", "-P", port.ToString(), "-i", identityFilePath,
             "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
             "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
             "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10",
             $"{username}@{hostname}"],
            RequiresSudo: true,
            Timeout: TimeSpan.FromSeconds(15),
            Description: "Diagnose failed SSHFS mount using saved-key SFTP access")
        {
            StandardInputBytes = Encoding.UTF8.GetBytes("cd \"" + remotePath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\npwd\n")
        };

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

    internal static SshfsMountHostCandidate BuildHostCandidate(ManagedHost host)
    {
        var hasPrivateKeyAuthentication = host.PrimaryAuthenticationType == AuthenticationType.PrivateKey ||
                                          host.FallbackAuthenticationType == AuthenticationType.PrivateKey;
        var hasStoredPrivateKey = !string.IsNullOrWhiteSpace(host.PrivateKeySecretReference);
        var hasPrivateKeyPassphrase = !string.IsNullOrWhiteSpace(host.PrivateKeyPassphraseSecretReference);
        var canPersist = hasPrivateKeyAuthentication &&
                       hasStoredPrivateKey &&
                       !hasPrivateKeyPassphrase &&
                       !string.IsNullOrWhiteSpace(host.Username) &&
                       !string.IsNullOrWhiteSpace(host.Hostname);

        var hasPassword = !string.IsNullOrWhiteSpace(host.PasswordSecretReference) &&
            (host.PrimaryAuthenticationType is AuthenticationType.Password or AuthenticationType.Conditional || host.FallbackAuthenticationType == AuthenticationType.Password);
        var canMount = canPersist || (hasPassword && !string.IsNullOrWhiteSpace(host.Username) && !string.IsNullOrWhiteSpace(host.Hostname));
        var status = canPersist
            ? "Stored SSH key available. LMS will verify access when mounting."
            : canMount ? "Saved username and password available for a temporary mount. Reconnecting after restart requires a private key."
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
            status) { CanPersistWithSshfs = canPersist, HasStoredPassword = hasPassword };
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
            return "Save a username and password for a temporary SSH mount, or a private key for a persistent mount.";
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

    internal static IReadOnlyList<string> BuildPasswordMountArguments(string source, string mountPath, int port) =>
        [source, mountPath, "-p", port.ToString(), "-o",
         "password_stdin,BatchMode=no,PasswordAuthentication=yes,PubkeyAuthentication=no,PreferredAuthentications=password,KbdInteractiveAuthentication=no,NumberOfPasswordPrompts=1,ConnectTimeout=10,StrictHostKeyChecking=accept-new,ServerAliveInterval=15,ServerAliveCountMax=3,allow_other"];

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
            "BatchMode=yes",
            "PasswordAuthentication=no",
            "KbdInteractiveAuthentication=no",
            "ConnectTimeout=10",
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
