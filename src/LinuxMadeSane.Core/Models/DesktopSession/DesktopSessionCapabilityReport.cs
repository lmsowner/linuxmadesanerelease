// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public sealed record DesktopSessionCapabilityReport(
    string UserName,
    int? UserId,
    string MachineName,
    int ProcessId,
    string? DesktopSessionId,
    DesktopSessionDisplayServer DisplayServer,
    string? Display,
    string? WaylandDisplay,
    string? XdgRuntimeDirectory,
    string? DesktopSession,
    string? CurrentDesktop,
    string? SessionType,
    string? SessionClass,
    string? SessionDesktop,
    bool HasDisplay,
    bool HasSessionBus,
    bool CanLaunchGuiApps,
    IReadOnlyList<string> AvailableTools,
    IReadOnlyList<string> MissingTools,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ReportedAtUtc)
{
    public bool IsGuiSessionAvailable => HasDisplay && HasSessionBus;

    public IReadOnlyDictionary<string, string> ReadOnlyDiagnostics { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
