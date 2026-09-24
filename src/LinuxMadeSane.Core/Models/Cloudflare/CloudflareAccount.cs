// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccount(
    string Id,
    string Name,
    string Type,
    string? Status);
