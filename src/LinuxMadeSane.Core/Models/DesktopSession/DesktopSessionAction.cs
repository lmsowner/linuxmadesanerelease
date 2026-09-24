// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public static class DesktopSessionActionKinds
{
    public const string SetKeyboardLayout = "keyboard.set-layout";
    public const string InstallAptPackages = "package.apt-install";
    public const string RepairAptSources = "package.apt-source-repair";
}

public sealed record DesktopSessionActionRequest(
    Guid RequestId,
    string ActionKind,
    IReadOnlyDictionary<string, string> Arguments,
    DateTimeOffset RequestedAtUtc);

public sealed record DesktopSessionActionResult(
    Guid RequestId,
    string ActionKind,
    bool Succeeded,
    string Summary,
    string Detail,
    IReadOnlyDictionary<string, string> Diagnostics,
    DateTimeOffset CompletedAtUtc);
