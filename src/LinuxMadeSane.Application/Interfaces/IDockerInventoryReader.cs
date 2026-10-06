// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Interfaces;
public sealed record DockerContainerObservation(string Name, bool Running, string Ports, string Networks);
public interface IDockerInventoryReader
{
    Task<IReadOnlyList<DockerContainerObservation>> GetContainersAsync(CancellationToken token = default);
}
