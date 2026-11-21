// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareSettings(
    Guid ManagedHostId,
    string AccountId,
    string AccountName,
    string ZoneId,
    string ZoneName,
    string? ApiTokenSecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
