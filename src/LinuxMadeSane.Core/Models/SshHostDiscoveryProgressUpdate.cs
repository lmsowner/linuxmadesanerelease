using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record SshHostDiscoveryProgressUpdate(
    SshHostDiscoveryStage Stage,
    string StatusMessage,
    int ProbedCount,
    int CandidateCount,
    int DiscoveredCount,
    string? CurrentRangeLabel,
    DiscoveredSshHost? NewHost);
