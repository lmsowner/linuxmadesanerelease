namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record SessionFileBackup(
    string SourcePath,
    string BackupPath,
    bool ExistedBeforeSnapshot);
