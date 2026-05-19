// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceInspectionResult(
    Guid ServiceId,
    string UnitName,
    string UnitFilePath,
    string ExecStart,
    string WorkingDirectory,
    string? EnvironmentFile,
    string RunningUser,
    string RunningGroup,
    string RestartPolicy,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LiveLogLines,
    int LastExitCode,
    ServiceHealthStatus HealthStatus,
    string UnitFileState,
    string LastKnownVersion,
    bool DaemonReloadRequired,
    bool RunningAsExpectedUser,
    bool WorkingDirectoryMatches,
    bool EnvironmentFileLoaded,
    bool PortAvailable,
    bool SymlinkTargetCurrent,
    bool BinaryTargetCurrent,
    bool StartsOutsideSystemdOnly,
    bool RestartLoopDetected,
    bool ConfigDriftDetected,
    IReadOnlyList<string> PlainEnglishSummary,
    IReadOnlyList<string> Findings);
