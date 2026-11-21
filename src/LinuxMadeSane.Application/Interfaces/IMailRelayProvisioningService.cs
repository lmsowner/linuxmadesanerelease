// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

using LinuxMadeSane.Core.Models.MailRelay;

public interface IMailRelayProvisioningService
{
    Task<IReadOnlyList<MailRelayListener>?> ReadListenersAsync(CancellationToken cancellationToken);
    Task<MailRelayDomain> CheckDomainDnsAsync(MailRelayDomain domain, CancellationToken cancellationToken);

    Task<MailRelayPublicIpSyncResult> SynchronizePublicIpAsync(
        string detectedPublicIp,
        CancellationToken cancellationToken = default);

    Task<MailRelaySetupPreview> PreviewAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default);

    Task<MailRelaySetupResult> ProvisionAsync(
        MailRelaySetupRequest request,
        IProgress<MailRelaySetupProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MailRelayLegacySubmissionResult> ConfigureLegacySubmissionAsync(
        MailRelayLegacySubmissionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MailRelayRemovalResult> RemoveAsync(
        MailRelayRemovalRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
