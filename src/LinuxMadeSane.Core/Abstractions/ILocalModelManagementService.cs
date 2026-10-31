// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalModelManagementService
{
    IReadOnlyList<LocalAiModelDefinition> ListDefinitions();
    IReadOnlyList<LocalAiModelDefinition> EvaluateForHardware(LocalAiHardwareProfile hardwareProfile);
    LocalAiModelDefinition? FindDefinition(string modelId);
    LocalAiModelDefinition Recommend(LocalAiHardwareProfile hardwareProfile);
    LocalAiCapabilityReport BuildCapabilityReport(string providerLabel, string modelId, bool toolUseEnabled);
}
