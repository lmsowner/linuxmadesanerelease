// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed class OnDemandAppsOptions
{
    public int IdleTimeoutSeconds { get; set; } = 300;
    public int CleanupIntervalSeconds { get; set; } = 30;
    public int MaximumActiveRoutes { get; set; } = 32;
    public int DnsPropagationTimeoutSeconds { get; set; } = 20;

    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(Math.Clamp(IdleTimeoutSeconds, 60, 3600));
    public TimeSpan CleanupInterval => TimeSpan.FromSeconds(Math.Clamp(CleanupIntervalSeconds, 10, 300));
    public TimeSpan DnsPropagationTimeout => TimeSpan.FromSeconds(Math.Clamp(DnsPropagationTimeoutSeconds, 5, 60));
}
