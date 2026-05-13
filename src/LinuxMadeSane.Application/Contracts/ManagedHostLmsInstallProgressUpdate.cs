using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public enum ManagedHostLmsInstallProgressState
{
    Starting,
    Output,
    Completed,
    Failed
}

public sealed record ManagedHostLmsInstallProgressUpdate(
    ManagedHostLmsInstallProgressState State,
    string Message,
    DateTimeOffset OccurredAtUtc,
    CommandExecutionOutputChannel? Channel = null);
