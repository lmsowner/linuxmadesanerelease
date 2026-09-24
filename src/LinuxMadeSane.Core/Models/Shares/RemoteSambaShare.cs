// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteSambaShare(
    string Name,
    string ShareType,
    string Comment,
    bool IsMountable,
    bool IsSpecial);
