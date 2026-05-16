// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderConnectionTester
{
    Task<AiProviderConnectionTestResult> TestAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);
}
