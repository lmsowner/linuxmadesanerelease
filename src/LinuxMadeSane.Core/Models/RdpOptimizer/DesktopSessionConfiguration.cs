namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DesktopSessionConfiguration(
    string DefaultSession,
    string XrdpSessionCommand,
    string? DisplayManager,
    bool XrdpUsesXfce,
    IReadOnlyList<string> TouchedFiles);
