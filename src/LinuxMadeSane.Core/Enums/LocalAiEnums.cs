// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum LocalAiRuntimeKind
{
    Unknown = 0,
    Ollama = 1,
    Docker = 2
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

public enum DockerAiEngineApiProfile
{
    Unknown = 0,
    OpenAiCompatible = 1,
    Ollama = 2
}
