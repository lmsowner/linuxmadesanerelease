// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

public sealed record ServiceWatchdogPolicy(string UnitName, bool Enabled = false, string EmailRecipient = "");
public sealed record ServiceWatchdogStatus(ServiceWatchdogPolicy Policy, int Attempts = 0,
    bool IncidentOpen = false, DateTimeOffset? LastAttemptUtc = null, string Message = "Not checked yet.");

public interface IServiceWatchdog
{
    Task<ServiceWatchdogStatus> GetAsync(string unitName, CancellationToken cancellationToken = default);
    Task SaveAsync(ServiceWatchdogPolicy policy, CancellationToken cancellationToken = default);
}
