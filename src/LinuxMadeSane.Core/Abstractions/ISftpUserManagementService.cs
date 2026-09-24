// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpUserManagementService
{
    Task<SftpConfigurationPlan> BuildCreateOrUpdatePlanAsync(
        SftpManagedUser user,
        string? newPassword,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> ApplyCreateOrUpdateAsync(
        SftpManagedUser user,
        string? newPassword,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> BuildDeletePlanAsync(
        string userName,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> DeleteAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> BuildDisablePlanAsync(
        string userName,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> DisableAsync(
        string userName,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<SftpConfigurationPlan> BuildPasswordResetPlanAsync(
        string userName,
        SftpAuthenticationMode authenticationMode,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<SftpApplyResult> ResetPasswordAsync(
        string userName,
        SftpAuthenticationMode authenticationMode,
        string newPassword,
        bool approved,
        CancellationToken cancellationToken = default);
}
