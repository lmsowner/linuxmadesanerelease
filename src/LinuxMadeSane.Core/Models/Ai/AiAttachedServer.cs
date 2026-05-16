// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiAttachedServer(
    Guid Id,
    Guid ThreadId,
    Guid ManagedHostId,
    string ServerName,
    string Hostname,
    string Environment,
    DateTimeOffset AttachedAtUtc);
