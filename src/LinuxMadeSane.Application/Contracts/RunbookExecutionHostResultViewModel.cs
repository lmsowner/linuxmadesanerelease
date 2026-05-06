namespace LinuxMadeSane.Application.Contracts;

public sealed record RunbookExecutionHostResultViewModel(
    Guid HostId,
    string HostName,
    bool Success,
    int ExitCode,
    string Summary,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
