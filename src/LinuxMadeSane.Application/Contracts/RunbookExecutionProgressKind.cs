namespace LinuxMadeSane.Application.Contracts;

public enum RunbookExecutionProgressKind
{
    Queued = 0,
    Started = 1,
    StandardOutput = 2,
    StandardError = 3,
    Completed = 4,
    Failed = 5
}
