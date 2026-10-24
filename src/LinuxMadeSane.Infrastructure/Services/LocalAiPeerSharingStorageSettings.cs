// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record LocalAiPeerSharingStorageSettings(string RootDirectory)
{
    public string SettingsPath => Path.Combine(RootDirectory, "peer-sharing.json");
}
