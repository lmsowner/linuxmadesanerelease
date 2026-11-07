// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderConnectionTester
{
    Task<AiProviderConnectionTestResult> TestAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);
}
