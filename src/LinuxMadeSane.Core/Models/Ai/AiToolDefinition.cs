// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
