namespace LinuxMadeSane.Application.Contracts;

public sealed record ManagedHostLmsInstallResult(
    bool Success,
    string Summary,
    string Detail,
    string CommandText,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
