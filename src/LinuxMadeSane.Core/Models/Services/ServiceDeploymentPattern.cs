// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceDeploymentPattern(
    DeploymentPatternType PatternType,
    string Name,
    string Summary,
    string PlainEnglishOutcome,
    IReadOnlyList<string> SaneDefaults,
    IReadOnlyList<string> GeneratedAssets);
