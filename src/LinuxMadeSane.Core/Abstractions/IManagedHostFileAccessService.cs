// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

// Guardrail: callers use this host-aware surface instead of branching on local-vs-SSH
// browsing in UI or feature code. Connection profiles carry transient secret handles only.
public interface IManagedHostFileAccessService
{
    Task<ManagedHostConnectionValidationResult> ValidateAccessAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task<string> GetHomeDirectoryAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task<FileSearchResponse> SearchAsync(
        ManagedHost host,
        FileSearchRequest request,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default);

    Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadStreamAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default) => Task.FromException<Stream>(new NotSupportedException("Seekable file reads are unavailable."));

    Task<SftpBinaryFileContent> ReadBinaryRangeAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        long offset,
        int maxBytes,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Range reads are unavailable.");

    Task DownloadFileAsync(
        ManagedHost host,
        string sourcePath,
        string localDestinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        ManagedHost host,
        string localSourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        ManagedHostConnectionProfile connectionProfile,
        bool createDirectories,
        string? encodingName = null,
        CancellationToken cancellationToken = default);

    Task<string> CreateDirectoryAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        bool recursive,
        CancellationToken cancellationToken = default);

    Task<string> CopyAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default,
        IProgress<FileTransferProgress>? progress = null);

    Task<string> MoveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task<string> CreateZipAsync(
        ManagedHost host,
        string sourcePath,
        string destinationZipPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task<string> CreateArchiveFromItemsAsync(
        ManagedHost host,
        string baseDirectory,
        IReadOnlyList<string> sourcePaths,
        string destinationArchivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default) => Task.FromException<string>(new NotSupportedException("Selected-file archives are unavailable."));

    Task<string> CreateArchiveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default) =>
        format == ArchiveFormat.Zip
            ? CreateZipAsync(host, sourcePath, destinationArchivePath, connectionProfile, cancellationToken)
            : Task.FromException<string>(new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} compression is not available for this file access service."));

    Task<string> ExtractZipAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);

    Task<string> ExtractArchiveAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default) =>
        format == ArchiveFormat.Zip
            ? ExtractZipAsync(host, archivePath, destinationDirectoryPath, connectionProfile, cancellationToken)
            : Task.FromException<string>(new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} extraction is not available for this file access service."));

    Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        ManagedHost host,
        string archivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        int maxEntries,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ArchiveEntry>>(new NotSupportedException("Archive preview is not available for this file access service."));

    Task SetOwnershipAndPermissionsAsync(
        ManagedHost host,
        FileOwnershipPermissionsChangeRequest request,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default);
}
