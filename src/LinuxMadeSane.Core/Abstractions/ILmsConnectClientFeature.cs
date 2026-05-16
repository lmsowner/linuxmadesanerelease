// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

public interface ILmsConnectClientFeature
{
    bool IsAvailable { get; }
    bool SupportsRemoteAiSharing { get; }
    Type? IntegrationPageComponentType { get; }
}
