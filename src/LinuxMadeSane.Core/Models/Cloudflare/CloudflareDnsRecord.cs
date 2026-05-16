// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareDnsRecord(
    string Id,
    string ZoneId,
    string Name,
    string Type,
    string Content,
    bool Proxied,
    int Ttl,
    string Comment,
    DateTimeOffset? ModifiedAtUtc);
