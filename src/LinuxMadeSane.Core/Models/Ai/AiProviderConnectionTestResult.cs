// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderConnectionTestResult(
    bool Succeeded,
    string Summary,
    string? Detail,
    DateTimeOffset CheckedAtUtc);
