using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiChatThreadListItemViewModel(
    Guid Id,
    string Title,
    string ProviderLabel,
    string ModelId,
    AiTrustLevel TrustLevel,
    int AttachedServerCount,
    int MessageCount,
    int PendingApprovalCount,
    DateTimeOffset UpdatedAtUtc);
