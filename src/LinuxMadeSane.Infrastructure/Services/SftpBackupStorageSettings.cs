// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record SftpBackupStorageSettings(string RootDirectory)
{
    public string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");
}
