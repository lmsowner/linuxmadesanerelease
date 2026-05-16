// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
