namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpBackupSnapshot(
    Guid Id,
    string Summary,
    IReadOnlyList<SftpBackupFile> Files,
    DateTimeOffset CreatedAtUtc,
    bool RollbackAvailable,
    string StorageDirectory);
