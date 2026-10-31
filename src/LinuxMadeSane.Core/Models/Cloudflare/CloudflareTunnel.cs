// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareTunnel(
    string Id,
    string AccountId,
    string Name,
    string ConfigSource,
    string Status,
    bool IsDeleted,
    bool IsManagedByLinuxMadeSane,
    DateTimeOffset CreatedAtUtc);
