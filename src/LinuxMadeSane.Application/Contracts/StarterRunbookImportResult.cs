namespace LinuxMadeSane.Application.Contracts;

public sealed record StarterRunbookImportResult(
    string HostName,
    int ImportedCount,
    int ExistingCount);
