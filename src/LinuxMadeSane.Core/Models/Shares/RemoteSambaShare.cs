// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteSambaShare(
    string Name,
    string ShareType,
    string Comment,
    bool IsMountable,
    bool IsSpecial);
