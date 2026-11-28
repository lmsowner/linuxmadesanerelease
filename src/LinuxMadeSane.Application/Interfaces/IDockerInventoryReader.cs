// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Interfaces;

public sealed record DockerPortBinding(int HostPort, int ContainerPort, string Protocol, string Address)
{
    public override string ToString() =>
        $"{(Address.Contains(':') ? $"[{Address}]" : Address)}:{HostPort} → {ContainerPort}/{Protocol}";
}
public sealed record DockerNetworkAddress(string Network, string Address);

public sealed record DockerContainerObservation(string Name, bool Running, string Ports, string Networks)
{
    public string Id { get; init; } = "";
    public bool ManualStartup { get; init; }
    public string NetworkMode { get; init; } = "";
    public string Image { get; init; } = "";
    public string State { get; init; } = "";
    public string? ComposeProject { get; init; }
    public string? SimpleConfig { get; init; }
    public string? HomeLabAppId { get; init; }
    public bool HomeLabLabel { get; init; }
    public bool IsRunning => Running;
    public IReadOnlyList<DockerNetworkAddress> Addresses { get; init; } = [];
    public IReadOnlyList<DockerPortBinding> Bindings { get; init; } = [];
}
public enum DockerAvailability { Ready, Missing, Stopped, Denied, Failed }
public sealed record DockerInventory(DockerAvailability Availability, IReadOnlyList<DockerContainerObservation> Containers);
public interface IDockerInventoryReader
{
    Task<IReadOnlyList<DockerContainerObservation>> GetContainersAsync(CancellationToken token = default);
    Task<DockerInventory> ReadInventoryAsync(CancellationToken token = default) => throw new NotSupportedException();
}
