using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record RestoreSnapshot(
    Guid SnapshotId,
    DateTimeOffset CreatedAt,
    RdpOptimizationProfile ProfileApplied,
    string UbuntuVersion,
    string? DisplayManager,
    IReadOnlyList<PackageState> RelevantPackages,
    IReadOnlyList<ServiceState> RelevantServices,
    DesktopSessionConfiguration SessionConfiguration,
    IReadOnlyList<SessionFileBackup> FileBackups,
    IReadOnlyList<string> RemovedPackages,
    IReadOnlyList<string> Notes);
