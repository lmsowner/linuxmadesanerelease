// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Infrastructure;
namespace LinuxMadeSane.Application.Interfaces;
public interface IUpsManagementService
{
    Task<UpsWorkspace> GetAsync(bool scanHardware = false, CancellationToken token = default);
    Task<UpsSnapshot> InspectAsync(string source, CancellationToken token = default);
    Task SavePolicyAsync(UpsPolicy policy, CancellationToken token = default);
    Task ConfigureLocalAsync(string name, string driver, string port, bool adoptExisting, CancellationToken token = default);
    Task MonitorAsync(CancellationToken token = default);
}
