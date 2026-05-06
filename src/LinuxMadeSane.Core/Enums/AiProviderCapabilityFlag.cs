namespace LinuxMadeSane.Core.Enums;

[Flags]
public enum AiProviderCapabilityFlag
{
    None = 0,
    BasicChat = 1 << 0,
    CommandExplanation = 1 << 1,
    LogSummary = 1 << 2,
    FixPlanGeneration = 1 << 3,
    ToolCalling = 1 << 4,
    Streaming = 1 << 5,
    DeepFixRecommended = 1 << 6,
    DeepFixAllowedWithExtraApproval = 1 << 7
}
