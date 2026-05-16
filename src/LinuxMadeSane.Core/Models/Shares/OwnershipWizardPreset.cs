// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record OwnershipWizardPreset(
    SharePresetType PresetType,
    string Name,
    string Summary,
    string Outcome,
    IReadOnlyList<string> RecommendedSteps);
