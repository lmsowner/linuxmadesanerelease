namespace LinuxMadeSane.Core.Models;

public enum CommandExecutionOutputChannel
{
    StandardOutput = 0,
    StandardError = 1
}

public abstract record CommandExecutionUpdate(DateTimeOffset OccurredAtUtc);

public sealed record CommandExecutionStartedUpdate(
    string CommandText,
    DateTimeOffset StartedAtUtc) : CommandExecutionUpdate(StartedAtUtc);

public sealed record CommandExecutionOutputUpdate(
    CommandExecutionOutputChannel Channel,
    string Content,
    bool IsCompleteSnapshot,
    DateTimeOffset OccurredAtUtc) : CommandExecutionUpdate(OccurredAtUtc);

public sealed record CommandExecutionCompletedUpdate(
    int ExitCode,
    DateTimeOffset CompletedAtUtc) : CommandExecutionUpdate(CompletedAtUtc);
