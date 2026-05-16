// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccessApplication(
    string Id,
    string AccountId,
    string Name,
    string Domain,
    string Type,
    string AudienceTag,
    string SessionDuration);
