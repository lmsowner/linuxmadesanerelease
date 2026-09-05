// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class SharesConfigurationSummaryProvider(ILinuxShareModuleDataService dataService)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Network Shares";

    public int SortOrder => 70;

    public string NavigationUrl => "/shares/manager";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var shares = await dataService.ListSharesAsync(cancellationToken);
        if (shares.Count == 0)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var accessPrincipals = shares
            .SelectMany(share => share.ValidUsers
                .Concat(share.ValidGroups)
                .Concat(share.WriteList)
                .Concat(share.ReadList))
            .Where(principal => !string.IsNullOrWhiteSpace(principal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new LmsConfigurationSummary(
            ModuleName,
            LmsConfigurationSummaryStatus.Configured,
            "LMS-managed Samba shares",
            [
                new("SMB shares", shares.Count.ToString()),
                new("Access principals", accessPrincipals.ToString()),
                new("Read only", shares.Count(share => share.ReadOnly).ToString()),
                new("Guest access", shares.Count(share => share.GuestAccess).ToString()),
                new("Browsable", shares.Count(share => share.Browseable).ToString())
            ],
            [],
            "/shares/manager",
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "LMS-managed Samba shares", [], [], "/shares/manager", SortOrder);
}
