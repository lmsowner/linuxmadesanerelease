// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
