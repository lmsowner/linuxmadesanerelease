// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Infrastructure;

namespace LinuxMadeSane.Application.Interfaces;

public interface IInfrastructureDiagnosticsService
{
    Task<DeviceInventory> DiscoverDevicesAsync(CancellationToken cancellationToken = default);
    Task<DeviceInventory> ProbeSubnetAsync(string listeningInterface, string subnet, CancellationToken cancellationToken = default);
    Task SaveDeviceAsync(NetworkDevice device, CancellationToken cancellationToken = default);
    Task WakeAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<ExposureSnapshot> GetExposureAsync(CancellationToken cancellationToken = default);
    Task<TimeDiagnostics> GetTimeAsync(CancellationToken cancellationToken = default);
    Task ControlTimeServiceAsync(bool enable, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmartHealth>> GetSmartAsync(CancellationToken cancellationToken = default);
    Task<FeaturePackageStatus> GetPackageStatusAsync(string feature, CancellationToken cancellationToken = default);
    Task InstallPackagesAsync(string feature, CancellationToken cancellationToken = default);
}
