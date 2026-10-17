// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISshdConfigService
{
    Task<bool> SupportsDropInConfigurationAsync(CancellationToken cancellationToken = default);

    string BuildManagedConfig(string basePath);

    Task<SftpValidationResult> ValidateConfigurationAsync(
        string proposedManagedConfig,
        bool useDropInConfiguration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpConfigurationAction>> BuildApplyActionsAsync(
        string proposedManagedConfig,
        bool useDropInConfiguration,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> ApplyManagedConfigurationAsync(
        string proposedManagedConfig,
        bool useDropInConfiguration,
        SftpBackupSnapshot backupSnapshot,
        CancellationToken cancellationToken = default);
}
