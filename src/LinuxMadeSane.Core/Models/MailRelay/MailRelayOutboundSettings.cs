// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Models.MailRelay;

public enum MailRelayOutboundMode { DirectPort25 = 0, AuthenticatedPort587 = 1, Automatic = 2, DirectPort587 = 3, AutomaticDirect = 4, MicrosoftGraph = 5, Gmail = 6, AmazonSes = 7 }

// Passwords are held only in ISecretStore, never in configuration or job snapshots.
public sealed record MailRelayOutboundSettings
{
    public MailRelayOutboundMode Mode { get; init; }
    public int DestinationPort { get; init; } = 25;
    public string Host { get; init; } = "";
    public string Username { get; init; } = "";
    public string? PasswordSecretReference { get; init; }
    public MailRelayApiSettings Api { get; init; } = new();
    public bool UsesHttpsApi => Mode is MailRelayOutboundMode.MicrosoftGraph or MailRelayOutboundMode.Gmail or MailRelayOutboundMode.AmazonSes;
    public string SpfInclude { get; init; } = "";
}

public enum MicrosoftMailSetupStage { None = 0, ApplicationRegistered = 1, MailboxAuthorised = 2, ExchangeAuthenticated = 3 }

// Only secret references are persisted here. Secret values use the existing encrypted store.
public sealed record MailRelayApiSettings
{
    public string SenderAddress { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string MicrosoftMailboxObjectId { get; init; } = "";
    public string MicrosoftServicePrincipalId { get; init; } = "";
    public string MicrosoftRegistrationMailbox { get; init; } = "";
    public MicrosoftMailSetupStage MicrosoftSetupStage { get; init; }
    public string MicrosoftSetupIssue { get; init; } = "";
    public string MicrosoftSetupDiagnostic { get; init; } = "";
    public string? CertificateSecretReference { get; init; }
    public string? ClientSecretReference { get; init; }
    public string? RefreshTokenReference { get; init; }
    public bool WorkspaceDelegation { get; init; }
    public string? ServiceAccountSecretReference { get; init; }
    public string Region { get; init; } = "eu-west-2";
    public string SendingIdentity { get; init; } = "";
    // Empty means the AWS SDK role/environment credential chain, not anonymous access.
    public string? AwsCredentialsSecretReference { get; init; }
    public string? BridgeSecretReference { get; init; }
}
