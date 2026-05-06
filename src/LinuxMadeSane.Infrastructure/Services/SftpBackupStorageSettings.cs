namespace LinuxMadeSane.Infrastructure.Services;

public sealed record SftpBackupStorageSettings(string RootDirectory)
{
    public string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");
}
