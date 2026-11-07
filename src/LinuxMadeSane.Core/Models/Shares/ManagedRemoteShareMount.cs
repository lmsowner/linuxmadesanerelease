// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record ManagedRemoteShareMount(
    Guid Id,
    string RemoteHost,
    string? RemoteAddress,
    string ShareName,
    string LocalMountPath,
    string? UserName,
    string? Domain,
    string? LocalOwner,
    string? FileMode,
    string? DirectoryMode,
    bool HasSavedPassword,
    bool IsMounted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastMountedAtUtc,
    string StatusMessage,
    string RemotePath = "")
{
    public string RemoteUncPath => $"//{RemoteHost}/{ShareName}" +
        (string.IsNullOrWhiteSpace(RemotePath) ? "" : "/" + RemotePath.Trim('/'));
}
