namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record LinuxCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool RequiresSudo,
    TimeSpan Timeout,
    string Description,
    string? WorkingDirectory = null)
{
    public bool IsOptionalExternalTool { get; init; }
}
