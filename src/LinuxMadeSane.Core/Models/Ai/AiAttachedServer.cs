// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiAttachedServer(
    Guid Id,
    Guid ThreadId,
    Guid ManagedHostId,
    string ServerName,
    string Hostname,
    string Environment,
    DateTimeOffset AttachedAtUtc);
