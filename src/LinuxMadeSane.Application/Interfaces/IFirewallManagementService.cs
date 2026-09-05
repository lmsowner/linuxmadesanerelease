// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface IFirewallManagementService
{
    Task<FirewallStatusViewModel> GetStatusAsync(CancellationToken cancellationToken = default);

    Task InstallAsync(CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartEnableTrialAsync(CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartDisableTrialAsync(CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartAllowRuleTrialAsync(
        FirewallAllowRuleEditor editor,
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartRemoveRuleTrialAsync(
        int ruleNumber,
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartEditAllowRuleTrialAsync(
        int ruleNumber,
        FirewallAllowRuleEditor editor,
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartRemoveDisabledRuleTrialAsync(
        Guid disabledRuleId,
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartEditDisabledAllowRuleTrialAsync(
        Guid disabledRuleId,
        FirewallAllowRuleEditor editor,
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartRemoveAllRulesTrialAsync(
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartDisableAllowRuleTrialAsync(
        int ruleNumber,
        CancellationToken cancellationToken = default);

    Task<FirewallTrialViewModel> StartEnableAllowRuleTrialAsync(
        Guid disabledRuleId,
        CancellationToken cancellationToken = default);

    Task ConfirmTrialAsync(Guid trialId, CancellationToken cancellationToken = default);

    Task RevertTrialAsync(Guid trialId, CancellationToken cancellationToken = default);
}
