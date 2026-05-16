// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskUserOption(
    string UserName,
    string DisplayLabel,
    string Description,
    bool IsRoot);
