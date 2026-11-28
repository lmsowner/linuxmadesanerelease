// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccessPolicy(
    string Id,
    string ApplicationId,
    string Name,
    string Decision,
    IReadOnlyList<string> IncludeEmails,
    IReadOnlyList<string> IncludeEmailDomains);
