namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record LinuxCommandResult(
    string CommandText,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool WasDryRun);
