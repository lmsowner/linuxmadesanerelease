// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Enums;

public enum DeploymentPatternType
{
    AspNetKestrel = 0,
    NodeService = 1,
    PythonApp = 2,
    DockerBackedService = 3,
    WorkerService = 4
}
