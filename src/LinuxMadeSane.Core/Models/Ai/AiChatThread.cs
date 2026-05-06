using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatThread(
    Guid Id,
    string Title,
    string ProviderKey,
    AiProviderType ProviderType,
    string ModelId,
    AiTrustProfile TrustProfile,
    string ProviderConversationReference,
    string ProviderStateReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
