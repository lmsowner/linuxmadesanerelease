// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.LocalAi;

public sealed record LocalAiRuntime(
    LocalAiRuntimeKind Kind,
    string DisplayName,
    string ExecutablePath,
    string Version,
    string ServiceName,
    string ApiEndpoint,
    bool IsInstalled,
    bool IsServiceActive,
    bool IsServiceEnabled,
    bool IsApiReachable,
    LocalAiHealthState Health,
    string Detail,
    DateTimeOffset CheckedAtUtc);

public sealed record LocalAiGpuAdapter(
    string Vendor,
    string Name,
    bool IsNvidia,
    bool IsAmd,
    long? TotalVramBytes,
    bool IsCudaAvailable,
    bool IsRocmAvailable,
    string DriverVersion,
    string DetectionSummary);

public sealed record LocalAiHardwareProfile(
    string CpuModel,
    int PhysicalCoreCount,
    int LogicalCoreCount,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    long AvailableDiskBytes,
    LocalAiGpuAccelerationState GpuAccelerationState,
    IReadOnlyList<LocalAiGpuAdapter> Gpus,
    string Summary,
    DateTimeOffset CapturedAtUtc)
{
    public bool HasGpu => Gpus.Count > 0;
}

public sealed record LocalAiModelDefinition(
    string ModelId,
    string DisplayName,
    string Description,
    long EstimatedRamBytes,
    long? EstimatedVramBytes,
    bool SupportsTools,
    bool SupportsStreaming,
    bool Recommended,
    bool IsDefaultRecommendation,
    LocalAiModelSuitability Suitability,
    AiProviderCapabilityFlag Capabilities,
    string SuitabilityWarning);

public sealed record LocalAiInstalledModel(
    string ModelId,
    string DisplayName,
    long SizeBytes,
    string Digest,
    DateTimeOffset? ModifiedAtUtc,
    bool IsRunning,
    bool IsDefault,
    AiProviderCapabilityFlag Capabilities,
    string Detail);

public sealed record LocalAiCapabilityReport(
    string ProviderLabel,
    string ModelId,
    AiProviderCapabilityFlag Capabilities,
    bool SupportsToolCallingReliably,
    bool RequiresExtraApprovalForMutations,
    string Summary,
    string Warning);

public sealed record LocalAiBenchmarkResult(
    Guid Id,
    string ModelId,
    string PromptSummary,
    bool Succeeded,
    TimeSpan Duration,
    string Detail,
    DateTimeOffset ExecutedAtUtc);

public sealed record LocalAiUsageEntry(
    Guid Id,
    string ProviderKey,
    LocalAiUsageScope Scope,
    Guid? ConsumerOrganizationId,
    Guid? ConsumerInstanceId,
    string ConsumerDisplayName,
    string ModelId,
    bool Succeeded,
    TimeSpan Duration,
    int PromptCharacterCount,
    int OutputCharacterCount,
    bool UsedToolCalls,
    string ResultSummary,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public sealed record LocalAiAuditEntry(
    Guid Id,
    string EventType,
    string Scope,
    string Summary,
    string Detail,
    bool Succeeded,
    DateTimeOffset CreatedAtUtc);

public sealed record LocalAiShareAccessRule(
    Guid? OrganizationId,
    Guid? InstanceId,
    string Label);

public sealed record LocalAiEngineSettings(
    LocalAiRuntimeKind RuntimeKind,
    string RuntimeEndpoint,
    string DefaultModelId,
    string LocalProviderKey,
    bool SharingEnabled,
    bool AllowOrganizationInstances,
    IReadOnlyList<Guid> AllowedOrganizationIds,
    IReadOnlyList<Guid> AllowedInstanceIds,
    IReadOnlyList<string> AllowedModelIds,
    int MaxConcurrentRequests,
    int MaxQueuedRequests,
    int MaxRequestsPerMinute,
    int MaxPromptCharacters,
    int RequestTimeoutSeconds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSharedAtUtc);

public sealed record LocalAiEngineStatus(
    LocalAiRuntime Runtime,
    LocalAiHardwareProfile Hardware,
    LocalAiEngineSettings Settings,
    IReadOnlyList<LocalAiInstalledModel> InstalledModels,
    LocalAiCapabilityReport Capability,
    bool IsLocalProviderConfigured,
    bool IsSharedToOtherInstances,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CheckedAtUtc);

public sealed record LocalAiPlanAction(
    string Title,
    string Description,
    string CommandPreview,
    string ExpectedImpact,
    bool Mutating,
    bool RequiresSudo);

public sealed record LocalAiSetupPlan(
    string Summary,
    string Recommendation,
    long EstimatedDiskBytes,
    long EstimatedMemoryBytes,
    bool RequiresApproval,
    bool RollbackAvailable,
    string VerificationPlan,
    IReadOnlyList<LocalAiPlanAction> Actions);

public sealed record LocalAiApplyResult(
    bool Succeeded,
    string Summary,
    string Detail,
    bool RolledBack,
    IReadOnlyList<string> OutputLines,
    DateTimeOffset CompletedAtUtc);

public sealed record RemoteLmsAiEngineDescriptor(
    Guid OrganizationId,
    Guid ProviderInstanceId,
    string InstanceDisplayName,
    string EngineDisplayName,
    string DefaultModelId,
    IReadOnlyList<string> AllowedModelIds,
    LocalAiCapabilityReport Capability,
    string HardwareSummary,
    bool IsOnline,
    int MaxConcurrentRequests,
    int MaxQueuedRequests,
    int MaxRequestsPerMinute,
    int MaxPromptCharacters);

public sealed record RemoteLmsAiEngineReference(
    Guid OrganizationId,
    Guid ProviderInstanceId,
    string EngineDisplayName,
    string InstanceDisplayName,
    IReadOnlyList<string> AllowedModelIds);

public sealed record RemoteLmsAiEngineProviderMetadata(
    Guid OrganizationId,
    Guid ProviderInstanceId,
    string EngineDisplayName,
    string InstanceDisplayName,
    IReadOnlyList<string> AllowedModelIds);

public sealed record DockerAiRuntimeStatus(
    bool IsDockerInstalled,
    bool IsDockerReachable,
    bool IsPortainerDetected,
    string Version,
    string Detail,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CheckedAtUtc);

public sealed record DockerAiEngineCatalogItem(
    string EngineId,
    string DisplayName,
    string Description,
    string DockerImage,
    string DockerTag,
    string DockerHubUrl,
    string DocumentationUrl,
    string TrustLabel,
    DockerAiEngineApiProfile ApiProfile,
    int ContainerPort,
    int HostPort,
    string SuggestedContainerName,
    string DefaultModelId,
    bool RequiresGpu,
    bool RequiresModelArgument,
    string RunCommand,
    string Detail,
    bool IsHostPortAvailable = true,
    string HostPortDetail = "");

public sealed record DockerAiDiscoveredEngine(
    string EngineId,
    string ContainerId,
    string ContainerName,
    string Image,
    string Status,
    string BaseUrl,
    string DefaultModelId,
    IReadOnlyList<string> ModelIds,
    bool IsRunning,
    bool IsReachable,
    string Detail);

public sealed record DockerAiEngineWorkspace(
    DockerAiRuntimeStatus Docker,
    IReadOnlyList<DockerAiEngineCatalogItem> Catalog,
    IReadOnlyList<DockerAiDiscoveredEngine> DiscoveredEngines);

public sealed record DockerAiProviderRequest(
    string EngineId,
    string DisplayName,
    string BaseUrl,
    string ModelId,
    bool SetDefault);

public sealed record CustomOpenAiProviderRequest(
    string DisplayName,
    string BaseUrl,
    string ModelId,
    bool SetDefault);

public sealed record DockerAiEngineProviderMetadata(
    string EngineId,
    string DockerImage,
    string ContainerName,
    string TrustLabel,
    string ApiProfile);
