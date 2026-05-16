// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareMountRequest(
    string RemoteHost,
    string? RemoteAddress,
    string ShareName,
    string LocalMountPath,
    string? UserName,
    string? Password,
    string? Domain,
    bool PersistOnServer);
