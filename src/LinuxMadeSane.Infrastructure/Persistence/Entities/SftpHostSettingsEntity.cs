// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SftpHostSettingsEntity
{
    public int Id { get; set; }

    public bool IsManagedModeEnabled { get; set; }

    public string BasePath { get; set; } = string.Empty;

    public int DefaultAuthenticationMode { get; set; }

    public bool PreferDropInConfiguration { get; set; }

    public string ManagedConfigPath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastAppliedAtUtc { get; set; }
}
