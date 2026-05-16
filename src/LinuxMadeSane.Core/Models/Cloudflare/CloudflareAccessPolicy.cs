// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccessPolicy(
    string Id,
    string ApplicationId,
    string Name,
    string Decision,
    IReadOnlyList<string> IncludeEmails,
    IReadOnlyList<string> IncludeEmailDomains);
