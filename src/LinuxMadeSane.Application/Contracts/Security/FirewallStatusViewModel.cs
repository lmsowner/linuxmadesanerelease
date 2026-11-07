// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record FirewallStatusViewModel(
    bool IsInstalled,
    bool CanManage,
    bool IsActive,
    string IncomingPolicy,
    string OutgoingPolicy,
    string RoutedPolicy,
    string Logging,
    IReadOnlyList<FirewallRuleViewModel> Rules,
    IReadOnlyList<FirewallListeningPortViewModel> ListeningPorts,
    FirewallTrialViewModel? ActiveTrial,
    string? Warning);
