// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayProvisioningQueue
{
    Task<MailRelayProvisioningJobSnapshot> EnqueueAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default);

    MailRelayProvisioningJobSnapshot? GetJob(Guid jobId);
    MailRelayProvisioningJobSnapshot? GetLatestJob();
}
