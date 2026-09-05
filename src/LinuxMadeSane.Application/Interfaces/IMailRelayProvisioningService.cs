// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayProvisioningService
{
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
