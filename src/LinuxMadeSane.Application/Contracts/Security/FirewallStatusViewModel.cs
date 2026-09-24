// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
