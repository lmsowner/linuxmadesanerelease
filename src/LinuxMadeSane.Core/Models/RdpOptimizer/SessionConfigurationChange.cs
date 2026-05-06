namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record SessionConfigurationChange(
    string FilePath,
    string Description,
    string ContentPreview,
    bool RequiresBackup,
    bool IsDestructive);
