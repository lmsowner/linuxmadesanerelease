// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.LocalAi;

public sealed record RemoteAiProviderTurnRequestEnvelope(
    RemoteAiChatThreadEnvelope Thread,
    IReadOnlyList<RemoteAiAttachedServerEnvelope> AttachedServers,
    IReadOnlyList<RemoteAiInputItemEnvelope> InputItems,
    IReadOnlyList<RemoteAiToolDefinitionEnvelope> AvailableTools,
    bool StreamingEnabled,
    bool InternetResearchAllowed);

public sealed record RemoteAiChatThreadEnvelope(
    Guid Id,
    string Title,
    string ProviderKey,
    AiProviderType ProviderType,
    string ModelId,
    string ProviderConversationReference,
    string ProviderStateReference,
    AiTrustProfileEnvelope TrustProfile,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AiTrustProfileEnvelope(
    AiTrustLevel TrustLevel,
    bool AllowReadOnlyTools,
    bool AllowMutatingTools,
    bool RequireApprovalForMediumRisk,
    bool RequireApprovalForHighRisk);

public sealed record RemoteAiAttachedServerEnvelope(
    Guid ManagedHostId,
    string ServerName,
    string Hostname,
    string Environment);

public sealed record RemoteAiInputItemEnvelope(
    string Kind,
    AiChatMessageRole? Role,
    string Content,
    string ToolCallId,
    string ToolName,
    string OutputJson,
    string ArgumentsJson);

public sealed record RemoteAiToolDefinitionEnvelope(
    string Name,
    string Description,
    string ParametersJsonSchema,
    bool RequiresApproval,
    AiActionRiskLevel RiskLevel);
