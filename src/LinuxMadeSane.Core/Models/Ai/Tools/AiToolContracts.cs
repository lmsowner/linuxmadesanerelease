using LinuxMadeSane.Core.Enums;

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
