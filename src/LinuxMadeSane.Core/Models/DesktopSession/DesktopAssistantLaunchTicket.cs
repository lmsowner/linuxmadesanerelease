// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public sealed record DesktopAssistantLaunchTicket(
    string Token,
    string ReturnUrl,
    DateTimeOffset ExpiresAtUtc);
