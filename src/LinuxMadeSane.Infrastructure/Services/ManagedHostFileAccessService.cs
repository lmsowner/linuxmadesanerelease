using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

// Guardrail: UI code must go through this service for host file access. Local-vs-SSH
// routing and transient credential resolution belong here, not in pages or components.
public sealed class ManagedHostFileAccessService(
    ILocalFileBrowsingService localFileBrowsingService,
    ISftpFileBrowsingService sftpFileBrowsingService,
    ManagedHostSshCredentialResolver sshCredentialResolver,
    ITransientConnectionSecretStore transientConnectionSecretStore) : IManagedHostFileAccessService
{
    public async Task<ManagedHostConnectionValidationResult> ValidateAccessAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return new ManagedHostConnectionValidationResult(true, string.Empty);
        }

        var username = connectionProfile.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            return new ManagedHostConnectionValidationResult(false, "Provide a username to browse files.");
        }

        var secrets = transientConnectionSecretStore.Get(connectionProfile.SecretHandle);
        if (!string.IsNullOrWhiteSpace(secrets.Password) || !string.IsNullOrWhiteSpace(secrets.PrivateKey))
        {
            return new ManagedHostConnectionValidationResult(true, string.Empty);
        }

        if (!connectionProfile.PreferStoredCredentials)
        {
            return new ManagedHostConnectionValidationResult(false, "Provide a password or private key to browse files.");
        }

        var resolution = await sshCredentialResolver.TryResolveAsync(host, cancellationToken);
        return new ManagedHostConnectionValidationResult(resolution.Success, resolution.FailureMessage);
    }

    public Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.ListItemsAsync(host.DefaultWorkingDirectory, path, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ListItemsAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<FileSearchResponse> SearchAsync(
        ManagedHost host,
        FileSearchRequest request,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.SearchAsync(host.DefaultWorkingDirectory, request, progress, cancellationToken);
        }

        var accessRequest = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.SearchAsync(
            host,
            request,
            accessRequest.Username,
            accessRequest.Password,
            accessRequest.PrivateKey,
            accessRequest.PrivateKeyPassphrase,
            accessRequest.PreferStoredCredentials,
            progress,
            cancellationToken);
    }

    public Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.ReadFileAsync(host.DefaultWorkingDirectory, path, maxBytes, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ReadFileAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            maxBytes,
            cancellationToken);
    }

    public Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.ReadBinaryFileAsync(host.DefaultWorkingDirectory, path, maxBytes, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ReadBinaryFileAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            maxBytes,
            cancellationToken);
    }

    public Task DownloadFileAsync(
        ManagedHost host,
        string sourcePath,
        string localDestinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.DownloadFileAsync(host.DefaultWorkingDirectory, sourcePath, localDestinationPath, progress, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.DownloadFileAsync(
            host,
            sourcePath,
            localDestinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            progress,
            cancellationToken);
    }

    public Task UploadFileAsync(
        ManagedHost host,
        string localSourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.UploadFileAsync(host.DefaultWorkingDirectory, localSourcePath, destinationPath, progress, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.UploadFileAsync(
            host,
            localSourcePath,
            destinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            progress,
            cancellationToken);
    }

    public Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        ManagedHostConnectionProfile connectionProfile,
        bool createDirectories,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.WriteFileAsync(host.DefaultWorkingDirectory, path, content, createDirectories, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.WriteFileAsync(
            host,
            path,
            content,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            createDirectories,
            cancellationToken);
    }

    public Task<string> CreateDirectoryAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.CreateDirectoryAsync(host.DefaultWorkingDirectory, path, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CreateDirectoryAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task DeleteAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.DeleteAsync(host.DefaultWorkingDirectory, path, recursive, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.DeleteAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            recursive,
            cancellationToken);
    }

    public Task<string> CopyAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.CopyAsync(host.DefaultWorkingDirectory, sourcePath, destinationPath, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CopyAsync(
            host,
            sourcePath,
            destinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<string> MoveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.MoveAsync(host.DefaultWorkingDirectory, sourcePath, destinationPath, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.MoveAsync(
            host,
            sourcePath,
            destinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<string> CreateZipAsync(
        ManagedHost host,
        string sourcePath,
        string destinationZipPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
        => CreateArchiveAsync(
            host,
            sourcePath,
            destinationZipPath,
            ArchiveFormat.Zip,
            connectionProfile,
            cancellationToken);

    public Task<string> CreateArchiveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.CreateArchiveAsync(host.DefaultWorkingDirectory, sourcePath, destinationArchivePath, format, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CreateArchiveAsync(
            host,
            sourcePath,
            destinationArchivePath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<string> ExtractZipAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
        => ExtractArchiveAsync(
            host,
            archivePath,
            destinationDirectoryPath,
            ArchiveFormat.Zip,
            connectionProfile,
            cancellationToken);

    public Task<string> ExtractArchiveAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.ExtractArchiveAsync(host.DefaultWorkingDirectory, archivePath, destinationDirectoryPath, format, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ExtractArchiveAsync(
            host,
            archivePath,
            destinationDirectoryPath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        ManagedHost host,
        string archivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.ListArchiveEntriesAsync(host.DefaultWorkingDirectory, archivePath, format, maxEntries, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ListArchiveEntriesAsync(
            host,
            archivePath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            maxEntries,
            cancellationToken);
    }

    public Task SetOwnershipAndPermissionsAsync(
        ManagedHost host,
        FileOwnershipPermissionsChangeRequest request,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            return localFileBrowsingService.SetOwnershipAndPermissionsAsync(host.DefaultWorkingDirectory, request, cancellationToken);
        }

        var accessRequest = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.SetOwnershipAndPermissionsAsync(
            host,
            request,
            accessRequest.Username,
            accessRequest.Password,
            accessRequest.PrivateKey,
            accessRequest.PrivateKeyPassphrase,
            accessRequest.PreferStoredCredentials,
            cancellationToken);
    }

    private ManagedHostConnectionRequest BuildRemoteRequest(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile)
    {
        var secrets = transientConnectionSecretStore.Get(connectionProfile.SecretHandle);
        return new ManagedHostConnectionRequest(
            string.IsNullOrWhiteSpace(connectionProfile.Username) ? host.Username : connectionProfile.Username.Trim(),
            NullIfEmpty(secrets.Password),
            NullIfEmpty(secrets.PrivateKey),
            NullIfEmpty(secrets.PrivateKeyPassphrase),
            connectionProfile.PreferStoredCredentials);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ManagedHostConnectionRequest(
        string Username,
        string? Password,
        string? PrivateKey,
        string? PrivateKeyPassphrase,
        bool PreferStoredCredentials);
}
