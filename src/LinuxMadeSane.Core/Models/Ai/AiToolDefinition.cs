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
