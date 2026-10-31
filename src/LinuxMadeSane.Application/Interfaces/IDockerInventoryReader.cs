// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Interfaces;
public sealed record DockerContainerObservation(string Name, bool Running, string Ports, string Networks);
public interface IDockerInventoryReader
{
    Task<IReadOnlyList<DockerContainerObservation>> GetContainersAsync(CancellationToken token = default);
}
