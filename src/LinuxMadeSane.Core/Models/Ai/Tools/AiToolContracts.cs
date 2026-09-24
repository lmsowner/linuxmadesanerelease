// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Core.Models.Ai;

public interface IAiToolRequest;
public interface IAiToolResponse;

public sealed record ListServersToolRequest(bool IncludeUnattachedServers = false) : IAiToolRequest;

public sealed record ListServersToolResponse(
    IReadOnlyList<AiToolServerListItem> Servers,
    int AttachedServerCount) : IAiToolResponse;

public sealed record GetServerSummaryToolRequest(Guid ServerId) : IAiToolRequest;

public sealed record GetServerSummaryToolResponse(
    AiToolServerSummary Server) : IAiToolResponse;

public sealed record GetServerHealthToolRequest(Guid ServerId) : IAiToolRequest;

public sealed record GetServerHealthToolResponse(
    Guid ServerId,
    string ServerName,
    string Platform,
    HostOperatingStatus OperatingStatus,
    ConnectionTestStatus ConnectionStatus,
    string Hostname,
    int Port,
    string Summary,
    string RawOutput,
    DateTimeOffset CapturedAtUtc) : IAiToolResponse;

public sealed record ListServicesToolRequest(
    Guid ServerId,
    string? Filter) : IAiToolRequest;

public sealed record ListServicesToolResponse(
    Guid ServerId,
    string ServerName,
    IReadOnlyList<AiToolServiceListItem> Services,
    string RawOutput) : IAiToolResponse;

public sealed record RestartServiceToolRequest(
    Guid ServerId,
    string ServiceName) : IAiToolRequest;

public sealed record RestartServiceToolResponse(
    Guid ServerId,
    string ServerName,
    string ServiceName,
    bool Succeeded,
    string CommandText,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    DateTimeOffset CompletedAtUtc) : IAiToolResponse;

public sealed record BrowseDirectoryToolRequest(
    Guid ServerId,
    string Path) : IAiToolRequest;

public sealed record BrowseDirectoryToolResponse(
    Guid ServerId,
    string ServerName,
    string Path,
    IReadOnlyList<AiToolDirectoryItem> Items) : IAiToolResponse;

public sealed record ReadFileToolRequest(
    Guid ServerId,
    string Path,
    int MaxBytes = 32768) : IAiToolRequest;

public sealed record ReadFileToolResponse(
    Guid ServerId,
    string ServerName,
    string Path,
    string Content,
    long SizeBytes,
    bool IsTruncated,
    DateTimeOffset? LastModifiedUtc) : IAiToolResponse;

public sealed record RunCommandToolRequest(
    Guid ServerId,
    string CommandText,
    string? WorkingDirectory = null) : IAiToolRequest;

public sealed record RunCommandToolResponse(
    Guid ServerId,
    string ServerName,
    string CommandText,
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc) : IAiToolResponse;

public sealed record WriteFileWithConfirmationToolRequest(
    Guid ServerId,
    string Path,
    string Content,
    bool CreateDirectories = false) : IAiToolRequest;

public sealed record WriteFileWithConfirmationToolResponse(
    Guid ServerId,
    string ServerName,
    string Path,
    long BytesWritten,
    string Mode,
    DateTimeOffset CompletedAtUtc) : IAiToolResponse;

public sealed record InstallPackageWithConfirmationToolRequest(
    Guid ServerId,
    IReadOnlyList<string> PackageNames) : IAiToolRequest;

public sealed record InstallPackageWithConfirmationToolResponse(
    Guid ServerId,
    string ServerName,
    IReadOnlyList<string> PackageNames,
    bool Succeeded,
    string CommandText,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc) : IAiToolResponse;

public sealed record SearchWebToolRequest(
    string Query,
    IReadOnlyList<string>? Domains = null,
    int MaxResults = 5) : IAiToolRequest;

public sealed record SearchWebToolResponse(
    string Query,
    IReadOnlyList<AiWebSearchResult> Results,
    DateTimeOffset SearchedAtUtc) : IAiToolResponse;

public sealed record FetchWebPageToolRequest(
    string Url,
    int MaxCharacters = 12000) : IAiToolRequest;

public sealed record FetchWebPageToolResponse(
    string Url,
    string? Title,
    string Content,
    bool IsTruncated,
    DateTimeOffset RetrievedAtUtc) : IAiToolResponse;

public sealed record AiWebSearchResult(
    string Title,
    string Url,
    string Snippet,
    string SourceDomain);

public sealed record InspectHomeLabToolRequest() : IAiToolRequest;

public sealed record InspectHomeLabToolResponse(
    IReadOnlyList<HomeLabAiGateway> VpnGateways,
    IReadOnlyList<HomeLabAiInstallation> Installations,
    IReadOnlyList<HomeLabAiPromptRecipe> PromptRecipes) : IAiToolResponse;

public sealed record InspectHomeLabApplicationConfigToolRequest(
    Guid InstallationId,
    string? RelativePath = null) : IAiToolRequest;

public sealed record InspectHomeLabApplicationConfigToolResponse(
    Guid InstallationId,
    string Name,
    IReadOnlyList<HomeLabApplicationConfigFile> Files,
    string Detail) : IAiToolResponse;

public sealed record RepairHomeLabApplicationConfigToolRequest(
    Guid InstallationId,
    string RelativePath,
    string ExpectedSha256,
    string ExpectedText,
    string ReplacementText) : IAiToolRequest;

public sealed record RepairHomeLabApplicationConfigToolResponse(
    Guid InstallationId,
    string Name,
    HomeLabApplicationConfigRepairResult Result) : IAiToolResponse;

public sealed record RepairHomeLabInstallationToolRequest(
    Guid InstallationId,
    bool RestoreContainerSettings = false) : IAiToolRequest;

public sealed record RepairHomeLabInstallationToolResponse(
    Guid InstallationId,
    string Name,
    string Action,
    bool Succeeded,
    HomeLabAiInstallation Before,
    HomeLabAiInstallation After,
    string Detail,
    DateTimeOffset CompletedAtUtc) : IAiToolResponse;

public sealed record ApplyHomeLabPromptRecipeToolRequest(
    string PromptRecipeId,
    Guid? VpnGatewayInstallationId = null) : IAiToolRequest;

public sealed record ApplyHomeLabPromptRecipeToolResponse(
    string PromptRecipeId,
    string PromptRecipeName,
    bool Succeeded,
    IReadOnlyList<HomeLabAiInstallation> Installations,
    IReadOnlyList<string> Details,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string>? RequiredStorageRoles = null,
    IReadOnlyList<HomeLabAiGateway>? RequiredVpnGateways = null) : IAiToolResponse;

public sealed record HomeLabAiGateway(
    Guid InstallationId,
    string Name,
    string HealthState,
    string HealthDetail);

public sealed record HomeLabAiInstallation(
    Guid InstallationId,
    Guid DeploymentId,
    string AppId,
    string Name,
    string ContainerName,
    string Image,
    string NetworkMode,
    IReadOnlyList<string> Dependencies,
    string HealthState,
    string HealthDetail,
    bool IsVpnRouted,
    bool IsSecured,
    string AccessUrl,
    IReadOnlyList<HomeLabAiPort> Ports,
    string PortForwardingStatus,
    int? ForwardedPort,
    string PortForwardingDetail,
    IReadOnlyList<HomeLabAiEndpoint>? Endpoints = null);

public sealed record HomeLabAiEndpoint(
    string ServiceId,
    string PortName,
    string Scope,
    string Url,
    string RoutingMode,
    string HealthState,
    string HealthDetail);

public sealed record HomeLabAiPort(
    string Name,
    int ContainerPort,
    int HostPort);

public sealed record HomeLabAiPromptRecipe(
    string Id,
    string Name,
    string Description,
    string Category,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> AppIds,
    bool RequiresPlanning,
    bool RequiresVpnGateway,
    IReadOnlyList<string> VpnRoutedAppIds);

public sealed record RollbackSafeChangeToolRequest(
    Guid OriginalActionId) : IAiToolRequest;

public sealed record RollbackSafeChangeToolResponse(
    Guid OriginalActionId,
    string Summary,
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    DateTimeOffset CompletedAtUtc) : IAiToolResponse;

public sealed record DesktopSetKeyboardLayoutToolRequest(
    string Layout) : IAiToolRequest;

public sealed record DesktopInstallAptPackagesToolRequest(
    IReadOnlyList<string> PackageNames) : IAiToolRequest;

public sealed record DesktopActionProposalToolResponse(
    string ActionKind,
    string Summary) : IAiToolResponse;

public sealed record SafeChangeFailureToolResponse(
    string Message) : IAiToolResponse;

public sealed record AiToolServerListItem(
    Guid ServerId,
    string Name,
    string Hostname,
    int Port,
    string Environment,
    string Platform,
    HostOperatingStatus OperatingStatus,
    ConnectionTestStatus ConnectionStatus,
    bool IsAttachedToThread);

public sealed record AiToolServerSummary(
    Guid ServerId,
    string Name,
    string Hostname,
    int Port,
    string Environment,
    string Description,
    string DefaultWorkingDirectory,
    string Platform,
    HostOperatingStatus OperatingStatus,
    ConnectionTestStatus ConnectionStatus,
    DateTimeOffset? LastSeenUtc,
    bool HasStoredPassword,
    bool HasStoredPrivateKey,
    bool UseKeyboardInteractiveFallback);

public sealed record AiToolServiceListItem(
    string UnitName,
    string LoadState,
    string ActiveState,
    string SubState,
    string Description);

public sealed record AiToolDirectoryItem(
    string Name,
    string FullPath,
    SftpItemType ItemType,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string Permissions,
    string OwnerName = "",
    string GroupName = "",
    string PermissionsOctal = "",
    string LinkTarget = "");
