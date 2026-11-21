// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
