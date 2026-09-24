// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record LocalAiPeerSharingStorageSettings(string RootDirectory)
{
    public string SettingsPath => Path.Combine(RootDirectory, "peer-sharing.json");
}
