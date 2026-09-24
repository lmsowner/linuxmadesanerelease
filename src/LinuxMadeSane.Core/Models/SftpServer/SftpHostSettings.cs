// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpHostSettings(
    bool IsManagedModeEnabled,
    string BasePath,
    SftpAuthenticationMode DefaultAuthenticationMode,
    bool PreferDropInConfiguration,
    string ManagedConfigPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastAppliedAtUtc);
