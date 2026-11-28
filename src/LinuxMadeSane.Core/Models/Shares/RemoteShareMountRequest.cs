// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareMountRequest(
    string RemoteHost,
    string? RemoteAddress,
    string ShareName,
    string LocalMountPath,
    string? UserName,
    string? Password,
    string? Domain,
    bool PersistOnServer,
    string? LocalOwner = null,
    string? FileMode = null,
    string? DirectoryMode = null,
    string? RemotePath = null);
