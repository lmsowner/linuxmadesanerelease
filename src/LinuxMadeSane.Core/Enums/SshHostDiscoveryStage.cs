// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
