// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpHostConfiguration(
    SftpHostSettings Settings,
    bool IsOpenSshInstalled,
    string OpenSshVersion,
    bool IsSshServiceActive,
    string SshServiceName,
    bool SupportsDropInConfiguration,
    string EffectiveManagedConfigPath,
    string MainConfigPath,
    bool IsManagedConfigPresent,
    bool IsBasePathPresent,
    bool IsConfigurationValid,
    string ValidationSummary,
    int ManagedUserCount,
    IReadOnlyList<string> MissingGroups,
    IReadOnlyList<string> Warnings);
