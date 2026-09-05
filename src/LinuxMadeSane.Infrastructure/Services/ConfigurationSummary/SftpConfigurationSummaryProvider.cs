// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class SftpConfigurationSummaryProvider(ISftpServerStore store)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "SFTP";

    public int SortOrder => 60;

    public string NavigationUrl => "/sftp-server";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        var users = await store.ListUsersAsync(cancellationToken);
        if (!settings.IsManagedModeEnabled && users.Count == 0)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var status = settings.IsManagedModeEnabled
            ? LmsConfigurationSummaryStatus.Configured
            : LmsConfigurationSummaryStatus.Disabled;
        return new LmsConfigurationSummary(
            ModuleName,
            status,
            "Managed file transfer over SSH",
            [
                new("Status", settings.IsManagedModeEnabled ? "Enabled" : "Disabled"),
                new("Users", users.Count.ToString()),
                new("Enabled users", users.Count(user => user.IsEnabled).ToString()),
                new("Root", settings.BasePath),
                new("Default authentication", SummaryValueFormatter.Words(settings.DefaultAuthenticationMode.ToString())),
                new("SSH configuration", settings.PreferDropInConfiguration ? "Managed drop-in" : "Main configuration")
            ],
            [],
            "/sftp-server",
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Managed file transfer over SSH", [], [], "/sftp-server", SortOrder);
}
