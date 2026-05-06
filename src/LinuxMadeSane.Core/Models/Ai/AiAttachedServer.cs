namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiAttachedServer(
    Guid Id,
    Guid ThreadId,
    Guid ManagedHostId,
    string ServerName,
    string Hostname,
    string Environment,
    DateTimeOffset AttachedAtUtc);
