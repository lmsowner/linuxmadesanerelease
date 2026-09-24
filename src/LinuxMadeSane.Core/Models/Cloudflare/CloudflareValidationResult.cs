// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareValidationResult(
    bool HasSavedToken,
    IReadOnlyList<CloudflareAccount> Accounts,
    IReadOnlyList<CloudflareZone> Zones);
