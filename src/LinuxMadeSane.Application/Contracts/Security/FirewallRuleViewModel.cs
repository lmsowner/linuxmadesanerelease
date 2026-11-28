// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record FirewallRuleViewModel(
    int? Number,
    string Destination,
    string Action,
    string Source,
    bool IsIpv6,
    string Comment,
    string RawText)
{
    public FirewallAllowRuleEditor? EditableAllowRule { get; init; }

    public Guid? DisabledRuleId { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool CanToggle =>
        EditableAllowRule is not null &&
        (Number.HasValue || DisabledRuleId.HasValue);
}
