// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalAiHardwareInspectionService
{
    Task<LocalAiHardwareProfile> InspectAsync(CancellationToken cancellationToken = default);
}
