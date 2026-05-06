namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class TerminalAiPromptRequest
{
    public TerminalAiPromptMode Mode { get; set; } = TerminalAiPromptMode.Custom;

    public Guid HostId { get; set; }

    public string HostName { get; set; } = string.Empty;

    public string HostAddress { get; set; } = string.Empty;

    public string HostEnvironment { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string TerminalOutput { get; set; } = string.Empty;

    public string Request { get; set; } = string.Empty;

    public bool AllowInternetResearch { get; set; }
}
