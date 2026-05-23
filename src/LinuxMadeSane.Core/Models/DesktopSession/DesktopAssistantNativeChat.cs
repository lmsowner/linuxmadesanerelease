// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public sealed record DesktopAssistantNativeCreateSessionRequest(
    string? ProviderKey,
    string? ModelId);

public sealed record DesktopAssistantNativeDeleteSessionRequest(
    Guid SessionId);

public sealed record DesktopAssistantNativeSendMessageRequest(
    Guid? SessionId,
    string Message,
    string? ProviderKey,
    string? ModelId);

public sealed record DesktopAssistantNativeApproveFixRequest(
    Guid? SessionId,
    DesktopAssistantNativeProposedFix Fix,
    string? ProviderKey,
    string? ModelId);

public sealed record DesktopAssistantNativeWorkspaceResponse(
    IReadOnlyList<DesktopAssistantNativeChatSession> Sessions,
    Guid? ActiveSessionId,
    IReadOnlyList<DesktopAssistantNativeChatMessage> Messages,
    bool IsReady,
    bool HasProvider,
    string ActiveProviderKey,
    string ProviderLabel,
    string ModelId,
    string StatusSummary,
    IReadOnlyList<DesktopAssistantNativeProvider> Providers,
    IReadOnlyList<DesktopAssistantNativeModel> Models,
    DesktopAssistantNativeTheme Theme,
    DesktopAssistantNativeProposedFix? ProposedFix = null);

public sealed record DesktopAssistantNativeChatSession(
    Guid Id,
    string Title,
    string ProviderKey,
    string ProviderLabel,
    string ModelId,
    int MessageCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record DesktopAssistantNativeChatMessage(
    Guid Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record DesktopAssistantNativeProposedFix(
    string Kind,
    IReadOnlyDictionary<string, string> Arguments,
    string Title,
    string Description);

public sealed record DesktopAssistantNativeProvider(
    string ProviderKey,
    string DisplayName,
    bool IsDefault,
    string DefaultModelId);

public sealed record DesktopAssistantNativeModel(
    string ProviderKey,
    string ModelId,
    string DisplayName);

public sealed record DesktopAssistantNativeTheme(
    string PaletteId,
    string PaletteName,
    string Mode,
    string Scheme,
    int FontScalePercent);
