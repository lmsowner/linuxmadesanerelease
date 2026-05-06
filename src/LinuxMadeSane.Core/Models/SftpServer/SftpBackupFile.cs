namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpBackupFile(
    string SourcePath,
    string BackupPath,
    bool ExistedBeforeSnapshot);
