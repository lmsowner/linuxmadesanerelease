// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DisabledLmsConnectClientFeature : ILmsConnectClientFeature
{
    public bool IsAvailable => false;
    public bool SupportsRemoteAiSharing => false;
    public Type? IntegrationPageComponentType => null;
}
