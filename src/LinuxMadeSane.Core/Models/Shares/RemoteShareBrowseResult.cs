// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareBrowseResult(
    string Target,
    string ResolvedName,
    string? IpAddress,
    bool UsedAuthentication,
    bool RequiresAuthentication,
    string StatusMessage,
    IReadOnlyList<RemoteSambaShare> Shares,
    IReadOnlyList<string> Notes);
