// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolDefinition(
    string Name,
    string Description,
    Type RequestType,
    Type ResponseType,
    AiToolApprovalMetadata Approval,
    string ExecutionPath,
    bool IsProviderVisible = true)
{
    public string RequestTypeName => RequestType.Name;
    public string ResponseTypeName => ResponseType.Name;
}
