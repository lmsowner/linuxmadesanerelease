// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum LocalAiRuntimeKind
{
    Unknown = 0,
    Ollama = 1
}

public enum LocalAiHealthState
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Failure = 3
}

public enum LocalAiModelSuitability
{
    Recommended = 0,
    Supported = 1,
    Limited = 2,
    NotRecommended = 3
}

public enum LocalAiUsageScope
{
    Local = 0,
    Remote = 1
}

public enum LocalAiGpuAccelerationState
{
    Unknown = 0,
    NotDetected = 1,
    Available = 2,
    Confirmed = 3
}
