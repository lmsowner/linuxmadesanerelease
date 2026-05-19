// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalFileBrowsingService
{
    Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        string workingDirectory,
        string path,
        CancellationToken cancellationToken = default);

    Task<FileSearchResponse> SearchAsync(
        string workingDirectory,
        FileSearchRequest request,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SftpFileContent> ReadFileAsync(
        string workingDirectory,
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default);

    Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        string workingDirectory,
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default);

    Task DownloadFileAsync(
        string workingDirectory,
        string sourcePath,
        string localDestinationPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        string workingDirectory,
        string localSourcePath,
        string destinationPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SftpWriteResult> WriteFileAsync(
        string workingDirectory,
        string path,
        string content,
        bool createDirectories,
        CancellationToken cancellationToken = default);

    Task<string> CreateDirectoryAsync(
        string workingDirectory,
        string path,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string workingDirectory,
        string path,
        bool recursive,
        CancellationToken cancellationToken = default);

    Task<string> CopyAsync(
        string workingDirectory,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default,
        IProgress<FileTransferProgress>? progress = null);

    Task<string> MoveAsync(
        string workingDirectory,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<string> CreateZipAsync(
        string workingDirectory,
        string sourcePath,
        string destinationZipPath,
        CancellationToken cancellationToken = default);

    Task<string> CreateArchiveAsync(
        string workingDirectory,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        CancellationToken cancellationToken = default) =>
        format == ArchiveFormat.Zip
            ? CreateZipAsync(workingDirectory, sourcePath, destinationArchivePath, cancellationToken)
            : Task.FromException<string>(new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} compression is not available for this file browser."));

    Task<string> ExtractZipAsync(
        string workingDirectory,
        string archivePath,
        string destinationDirectoryPath,
        CancellationToken cancellationToken = default);

    Task<string> ExtractArchiveAsync(
        string workingDirectory,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        CancellationToken cancellationToken = default) =>
        format == ArchiveFormat.Zip
            ? ExtractZipAsync(workingDirectory, archivePath, destinationDirectoryPath, cancellationToken)
            : Task.FromException<string>(new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} extraction is not available for this file browser."));

    Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        string workingDirectory,
        string archivePath,
        ArchiveFormat format,
        int maxEntries,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ArchiveEntry>>(new NotSupportedException("Archive preview is not available for this file browser."));

    Task SetOwnershipAndPermissionsAsync(
        string workingDirectory,
        FileOwnershipPermissionsChangeRequest request,
        CancellationToken cancellationToken = default);
}
