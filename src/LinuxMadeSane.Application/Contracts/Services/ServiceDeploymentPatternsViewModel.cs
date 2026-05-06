using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceDeploymentPatternsViewModel(
    IReadOnlyList<ServiceDeploymentPattern> Patterns);
