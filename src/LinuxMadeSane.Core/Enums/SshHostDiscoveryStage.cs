// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
