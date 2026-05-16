// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
