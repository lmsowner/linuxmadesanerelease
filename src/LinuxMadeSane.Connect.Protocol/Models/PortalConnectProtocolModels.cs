// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Connect.Protocol.Models;

public static class PortalConnectConstants
{
    public const string CookieScheme = "lms.portal.cookie";
    public const string AzureAdScheme = "lms.portal.azuread";
    public const string HubPath = "/hubs/connect";
    public const string UserIdClaimType = "portal:user-id";
    public const string SystemAdminClaimType = "portal:is-system-admin";
}

public static class PortalInstanceApiHeaders
{
    public const string InstanceId = "X-LMS-Portal-Instance-Id";
    public const string ApiKeyId = "X-LMS-Portal-Api-Key-Id";
    public const string ApiSecret = "X-LMS-Portal-Api-Secret";
}

public enum PortalInstanceCommandType
{
    GetSystemSnapshot = 0,
    GetDashboardSummary = 1,
    GetServerListSummary = 2,
    GetVersionStatus = 3,
    ExecuteAiTurn = 4,
    OpenBrowserSession = 5,
    OpenBrowserTunnel = 6,
    CloseBrowserSession = 7,
    PrepareDirectAccess = 8,
    ActivateDirectAccess = 9,
    RenewDirectAccess = 10,
    CloseDirectAccess = 11
}

public sealed record PendingPortalConnectionHello(
    string PairingCode,
    string InstanceDisplayName,
    string MachineName,
    string AppVersion,
    string MachineFingerprint,
    DateTimeOffset CodeExpiresAtUtc,
    string SignedPairingRequestToken);

public sealed record PairedPortalConnectionHello(
    Guid InstanceId,
    string ApiKeyId,
    string ApiSecret,
    string InstanceDisplayName,
    string MachineName,
    string AppVersion,
    string MachineFingerprint);

public sealed record PortalPairingApprovedMessage(
    Guid InstanceId,
    Guid OrganizationId,
    string OrganizationName,
    string ApiKeyId,
    string ApiSecret,
    string InstanceDisplayName);

public sealed record PortalConnectionAck(
    bool Succeeded,
    string Message,
    Guid? InstanceId = null);

public sealed record PortalInstanceCommandEnvelope(
    Guid RequestId,
    PortalInstanceCommandType CommandType,
    string PayloadJson,
    DateTimeOffset RequestedAtUtc);

public sealed record PortalInstanceCommandResultEnvelope(
    Guid RequestId,
    PortalInstanceCommandType CommandType,
    bool Succeeded,
    string PayloadJson,
    string ErrorMessage,
    DateTimeOffset CompletedAtUtc);

public sealed record PortalInstanceSystemSnapshot(
    string InstanceDisplayName,
    string MachineName,
    string AppVersion,
    string OverallHealth,
    string HealthSummary,
    int OnlineManagedHostCount,
    int DegradedManagedHostCount,
    int OfflineManagedHostCount,
    int ManagedHostCount,
    int HealthyServiceCount,
    int WarningServiceCount,
    int BrokenServiceCount,
    int SavedServiceCount,
    int SecurityUserCount,
    DateTimeOffset GeneratedAtUtc);

public sealed record PortalInstanceServerSummary(
    string Name,
    string Hostname,
    string Environment,
    string OperatingStatus);

public sealed record PortalInstanceServerListSummary(
    int ServerCount,
    IReadOnlyList<PortalInstanceServerSummary> Servers,
    DateTimeOffset GeneratedAtUtc);

public sealed record PortalInstanceVersionStatus(
    string InstanceDisplayName,
    string MachineName,
    string AppVersion,
    string ConnectorStatus,
    string OverallHealth,
    DateTimeOffset GeneratedAtUtc);

public sealed record PortalSharedAiEngineSyncRequest(
    string EngineDisplayName,
    string DefaultModelId,
    IReadOnlyList<string> AllowedModelIds,
    bool SharingEnabled,
    bool AllowOrganizationInstances,
    IReadOnlyList<Guid> AllowedOrganizationIds,
    IReadOnlyList<Guid> AllowedInstanceIds,
    int MaxConcurrentRequests,
    int MaxQueuedRequests,
    int MaxRequestsPerMinute,
    int MaxPromptCharacters,
    int CapabilityFlags,
    string CapabilitySummary,
    string CapabilityWarning,
    string HardwareSummary,
    bool IsOnline,
    DateTimeOffset SyncedAtUtc);

public sealed record PortalSharedAiEngineDescriptor(
    Guid OrganizationId,
    Guid ProviderInstanceId,
    string InstanceDisplayName,
    string EngineDisplayName,
    string DefaultModelId,
    IReadOnlyList<string> AllowedModelIds,
    int CapabilityFlags,
    string CapabilitySummary,
    string CapabilityWarning,
    string HardwareSummary,
    bool IsOnline,
    int MaxConcurrentRequests,
    int MaxQueuedRequests,
    int MaxRequestsPerMinute,
    int MaxPromptCharacters);

public sealed record PortalAiEngineRelayRequest(
    Guid ConsumerOrganizationId,
    Guid ConsumerInstanceId,
    string ConsumerDisplayName,
    string ModelId,
    int PromptCharacterCount,
    string ProviderTurnRequestJson);

public sealed record PortalAiEngineRelayResponse(
    bool Succeeded,
    string ProviderTurnResultJson,
    string ErrorMessage,
    string Summary);

// Browser session controls travel only over the authenticated instance connector.
public sealed record PortalBrowserSessionRequest(Guid SessionId, string UserEmail, DateTimeOffset ExpiresAtUtc);
public sealed record PortalBrowserSessionResponse(string UpstreamCookie);
public sealed record PortalBrowserTunnelRequest(Guid SessionId, string Ticket);
public sealed record PortalBrowserSessionClose(Guid SessionId);

// Direct HTTPS access uses the existing authenticated command channel only.
public sealed record PortalDirectPrepare(Guid SessionId, string Hostname, string UserEmail, string PortalOrigin, DateTimeOffset ExpiresAtUtc);
public sealed record PortalDirectPrepared(string PublicAddress, int ExternalPort, int LeaseSeconds, string Protocol, string CsrBase64);
public sealed record PortalDirectActivate(Guid SessionId, string CertificatePem);
public sealed record PortalDirectReady(string Grant);
public sealed record PortalDirectLease(Guid SessionId);
