// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record OwnershipWizardPreset(
    SharePresetType PresetType,
    string Name,
    string Summary,
    string Outcome,
    IReadOnlyList<string> RecommendedSteps);
