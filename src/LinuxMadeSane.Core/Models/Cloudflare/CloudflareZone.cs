// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareZone(
    string Id,
    string Name,
    string AccountId,
    string AccountName,
    string Status,
    bool Paused);
