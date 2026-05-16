// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record ManagedRemoteShareMount(
    Guid Id,
    string RemoteHost,
    string? RemoteAddress,
    string ShareName,
    string LocalMountPath,
    string? UserName,
    string? Domain,
    bool HasSavedPassword,
    bool IsMounted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastMountedAtUtc,
    string StatusMessage);
