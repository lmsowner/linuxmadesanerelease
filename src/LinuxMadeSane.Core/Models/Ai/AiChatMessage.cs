using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatMessage(
    Guid Id,
    Guid ThreadId,
    int SequenceNumber,
    AiChatMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc);
