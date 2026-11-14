// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalAiHardwareInspectionService
{
    Task<LocalAiHardwareProfile> InspectAsync(CancellationToken cancellationToken = default);
}
