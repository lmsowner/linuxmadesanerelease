// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
