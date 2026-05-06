using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpFileBrowsingService
{
    Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);

    Task<FileSearchResponse> SearchAsync(
        ManagedHost host,
        FileSearchRequest request,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        int maxBytes,
        CancellationToken cancellationToken = default);

    Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        int maxBytes,
        CancellationToken cancellationToken = default);

    Task DownloadFileAsync(
        ManagedHost host,
        string sourcePath,
        string localDestinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        ManagedHost host,
        string localSourcePath,
        string destinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        bool createDirectories,
        CancellationToken cancellationToken = default);

    Task<string> CreateDirectoryAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        bool recursive,
        CancellationToken cancellationToken = default);

    Task<string> CopyAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);

    Task<string> MoveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);

    Task<string> CreateZipAsync(
        ManagedHost host,
        string sourcePath,
        string destinationZipPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);

    Task<string> CreateArchiveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default) =>
        format == ArchiveFormat.Zip
            ? CreateZipAsync(host, sourcePath, destinationArchivePath, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken)
            : Task.FromException<string>(new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} compression is not available for this SFTP browser."));

    Task<string> ExtractZipAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);

    Task<string> ExtractArchiveAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default) =>
        format == ArchiveFormat.Zip
            ? ExtractZipAsync(host, archivePath, destinationDirectoryPath, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken)
            : Task.FromException<string>(new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} extraction is not available for this SFTP browser."));

    Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        ManagedHost host,
        string archivePath,
        ArchiveFormat format,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        int maxEntries,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ArchiveEntry>>(new NotSupportedException("Archive preview is not available for this SFTP browser."));

    Task SetOwnershipAndPermissionsAsync(
        ManagedHost host,
        FileOwnershipPermissionsChangeRequest request,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default);
}
