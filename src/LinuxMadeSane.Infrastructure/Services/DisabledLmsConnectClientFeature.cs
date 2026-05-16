// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DisabledLmsConnectClientFeature : ILmsConnectClientFeature
{
    public bool IsAvailable => false;
    public bool SupportsRemoteAiSharing => false;
    public Type? IntegrationPageComponentType => null;
}
