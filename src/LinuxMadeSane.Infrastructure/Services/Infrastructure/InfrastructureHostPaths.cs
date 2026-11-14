// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services.Infrastructure;
public sealed record InfrastructureHostPaths(string ProtectionKeyDirectory, string ApplicationDirectory)
{
    public string HostConfigurationDirectory { get; init; } = "/etc";
}
