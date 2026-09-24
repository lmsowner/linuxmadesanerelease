// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayPublicIpMonitorService
{
    Task<MailRelayConfiguration> SaveSettingsAsync(
        MailRelayPublicIpMonitorSettingsRequest request,
        CancellationToken cancellationToken = default);

    Task<MailRelayPublicIpSyncResult> CheckNowAsync(CancellationToken cancellationToken = default);
}
