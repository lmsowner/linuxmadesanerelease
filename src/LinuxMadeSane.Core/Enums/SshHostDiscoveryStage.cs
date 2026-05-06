namespace LinuxMadeSane.Core.Enums;

public enum SshHostDiscoveryStage
{
    Preparing = 0,
    ReadingNeighbourCache = 1,
    ProbingNeighbourHosts = 2,
    ProbingPrioritizedBlocks = 3,
    ProbingRemainingBlocks = 4,
    Completed = 5
}
