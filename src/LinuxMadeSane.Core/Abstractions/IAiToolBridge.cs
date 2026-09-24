// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiToolBridge : IAiToolRegistry
{
    Task<AiToolExecutionResult> InvokeAsync(AiToolInvocation invocation, CancellationToken cancellationToken = default);
}
