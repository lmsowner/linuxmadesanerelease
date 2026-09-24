// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record SftpBackupStorageSettings(string RootDirectory)
{
    public string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");
}
