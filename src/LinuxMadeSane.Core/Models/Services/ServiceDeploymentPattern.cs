using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceDeploymentPattern(
    DeploymentPatternType PatternType,
    string Name,
    string Summary,
    string PlainEnglishOutcome,
    IReadOnlyList<string> SaneDefaults,
    IReadOnlyList<string> GeneratedAssets);
