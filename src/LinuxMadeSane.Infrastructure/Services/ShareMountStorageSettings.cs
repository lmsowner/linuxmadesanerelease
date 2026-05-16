// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class ShareMountStorageSettings
{
    public ShareMountStorageSettings(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public string CredentialsDirectory => Path.Combine(RootDirectory, "credentials");

    public string RuntimeDirectory => Path.Combine(RootDirectory, "runtime");

    public string TemporaryMountDirectory => Path.Combine(RuntimeDirectory, "temporary-mounts");

    public string StagingDirectory => Path.Combine(RootDirectory, "staging");
}
