// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public sealed record HomeLabEdgeGatewayRequest(
    string Hostname,
    string DomainName,
    EdgeGatewayAuthMode AuthMode = EdgeGatewayAuthMode.RequireMfa);

public sealed record HomeLabInstallRequest(
    string AppId,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? StoragePaths = null,
    IReadOnlyDictionary<string, string>? Configuration = null,
    HomeLabEdgeGatewayRequest? EdgeGateway = null,
    IReadOnlyDictionary<string, string>? SecretConfiguration = null);

public sealed record HomeLabRecipeInstallRequest(
    string RecipeId,
    IReadOnlyDictionary<string, string>? StoragePaths = null,
    IReadOnlyDictionary<string, string>? Configuration = null,
    IReadOnlySet<string>? AppIds = null,
    IReadOnlyDictionary<string, string>? SecretConfiguration = null);

public enum HomeLabLifecycleAction
{
    Start = 0,
    Stop = 1,
    Restart = 2,
    Update = 3,
    Remove = 4,
    RefreshHealth = 5,
    Repair = 6
}

public sealed record HomeLabOperationResult(
    bool Succeeded,
    string Summary,
    string Detail,
    IReadOnlyList<string> Output,
    HomeLabHealthState HealthState,
    DateTimeOffset CompletedAtUtc);

public sealed record HomeLabLogsResult(
    bool Succeeded,
    string Logs,
    string Error);
