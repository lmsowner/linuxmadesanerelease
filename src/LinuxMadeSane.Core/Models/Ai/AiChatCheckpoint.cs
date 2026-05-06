namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatCheckpoint(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    string Label,
    string Summary,
    string StateJson,
    DateTimeOffset CreatedAtUtc);
