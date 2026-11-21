// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

public interface ILmsConnectClientFeature
{
    bool IsAvailable { get; }
    bool SupportsRemoteAiSharing { get; }
    Type? IntegrationPageComponentType { get; }
}
