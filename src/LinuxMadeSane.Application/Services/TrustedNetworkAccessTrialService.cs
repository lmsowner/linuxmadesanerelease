// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public sealed class TrustedNetworkAccessTrialService
{
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private TrustedNetworkAccessTrial? activeTrial;

    public TrustedNetworkAccessTrialService()
        : this(TimeProvider.System)
    {
    }

    public TrustedNetworkAccessTrialService(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public static bool RequiresTrialForRuleChange(
        TrustedNetworkEntry? currentEntry,
        TrustedNetworkEntry plannedEntry)
    {
        ArgumentNullException.ThrowIfNull(plannedEntry);
        if (currentEntry is not { IsEnabled: true })
        {
            return false;
        }

        if (!plannedEntry.IsEnabled)
        {
            return true;
        }

        if (!currentEntry.IsAuthenticationEnabled && plannedEntry.IsAuthenticationEnabled)
        {
            return true;
        }

        return !string.Equals(
            currentEntry.AddressOrCidr.Trim(),
            plannedEntry.AddressOrCidr.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    public TrustedNetworkAccessTrial Begin(
        IReadOnlyList<TrustedNetworkEntry> plannedEntries,
        string summary,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(plannedEntries);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        lock (sync)
        {
            ClearExpiredTrial();
            if (activeTrial is not null)
            {
                throw new InvalidOperationException("Keep or revert the current network access test before starting another one.");
            }

            activeTrial = new TrustedNetworkAccessTrial(
                Guid.NewGuid(),
                string.IsNullOrWhiteSpace(summary) ? "Network access change" : summary.Trim(),
                timeProvider.GetUtcNow().Add(duration),
                plannedEntries.ToArray());
            return activeTrial;
        }
    }

    public IReadOnlyList<TrustedNetworkEntry> GetEffectiveEntries(
        IReadOnlyList<TrustedNetworkEntry> persistedEntries)
    {
        ArgumentNullException.ThrowIfNull(persistedEntries);
        lock (sync)
        {
            ClearExpiredTrial();
            return activeTrial?.PlannedEntries ?? persistedEntries;
        }
    }

    public TrustedNetworkAccessTrial? GetActive()
    {
        lock (sync)
        {
            ClearExpiredTrial();
            return activeTrial;
        }
    }

    public bool End(Guid trialId)
    {
        lock (sync)
        {
            ClearExpiredTrial();
            if (activeTrial?.Id != trialId)
            {
                return false;
            }

            activeTrial = null;
            return true;
        }
    }

    private void ClearExpiredTrial()
    {
        if (activeTrial is not null && activeTrial.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            activeTrial = null;
        }
    }
}

public sealed record TrustedNetworkAccessTrial(
    Guid Id,
    string Summary,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<TrustedNetworkEntry> PlannedEntries);
