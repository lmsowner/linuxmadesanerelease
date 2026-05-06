namespace LinuxMadeSane.Infrastructure.Services;

public sealed record RdpOptimizerStorageSettings(string RootDirectory)
{
    public string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");

    public string RunsDirectory => Path.Combine(RootDirectory, "runs");
}
