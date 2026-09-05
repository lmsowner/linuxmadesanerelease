// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Contracts.MailRelay;

public sealed record MailRelaySetupRequest(
    string CloudflareZoneId,
    string RelayHostname,
    string SendingDomain,
    string DkimSelector,
    string ApplicationName,
    string ApplicationUsername,
    bool AllowTailscale,
    bool AllowTrustedLan);

public sealed record MailRelayLegacySubmissionRequest(
    bool Enabled,
    IReadOnlyList<string> ListenAddresses,
    IReadOnlyList<string> AllowedNetworks);

public sealed record MailRelayLegacySubmissionResult(
    bool Success,
    MailRelayConfiguration? Configuration,
    string Summary);

public sealed record MailRelayRemovalRequest(
    bool RemoveContainer,
    bool RemoveApplicationCredentials,
    bool RemoveManagedDnsRecords,
    bool RemoveDkimKeys,
    bool Confirmed);

public sealed record MailRelayRemovalResult(
    bool Success,
    MailRelayConfiguration? Configuration,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    string Summary);

public enum MailRelaySetupChangeKind
{
    Create = 0,
    Update = 1,
    Keep = 2,
    Blocked = 3
}

public sealed record MailRelaySetupChange(
    string Purpose,
    string RecordType,
    string RecordName,
    string ProposedValue,
    MailRelaySetupChangeKind Kind,
    string Detail);

public enum MailRelayExistingProvider
{
    NoneDetected = 0,
    Microsoft365 = 1,
    GoogleWorkspace = 2,
    ExistingMailProvider = 3
}

public sealed record MailRelayExistingEmailConfiguration(
    string Domain,
    MailRelayExistingProvider Provider,
    IReadOnlyList<string> MxRecords,
    string? ExistingSpf,
    string ProposedSpf,
    int SpfDnsLookupTerms,
    IReadOnlyList<string> ExistingDkimRecords,
    string? ExistingDmarc,
    string? ExistingDmarcPolicy,
    MailRelayDeliveryMode DeliveryMode);

public sealed record MailRelaySetupPreview(
    MailRelaySetupRequest Request,
    MailRelayPreflightResult Preflight,
    IReadOnlyList<MailRelaySetupChange> DnsChanges,
    MailRelayExistingEmailConfiguration ExistingEmailConfiguration,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanInstall => Preflight.CanConfigure && Errors.Count == 0;
}

public enum MailRelaySetupStepState
{
    Pending = 0,
    Running = 1,
    Complete = 2,
    Failed = 3
}

public sealed record MailRelaySetupProgressUpdate(
    string Key,
    string Label,
    MailRelaySetupStepState State,
    string Detail);

public sealed record MailRelaySetupResult(
    bool Success,
    string RelayHostname,
    string SubmissionHost,
    int SubmissionPort,
    string Username,
    string? GeneratedPassword,
    string SendingDomain,
    string DkimSelector,
    bool OpenRelayTestPassed,
    IReadOnlyList<MailRelaySetupProgressUpdate> Steps,
    string Summary);

public enum MailRelayProvisioningJobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

public sealed record MailRelayProvisioningJobSnapshot(
    Guid Id,
    MailRelaySetupRequest Request,
    MailRelayProvisioningJobStatus Status,
    IReadOnlyList<MailRelaySetupProgressUpdate> Steps,
    MailRelaySetupResult? Result,
    string Summary,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc)
{
    public bool IsTerminal => Status is MailRelayProvisioningJobStatus.Succeeded or MailRelayProvisioningJobStatus.Failed;
}
