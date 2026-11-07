// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class TerminalAiPromptRequest
{
    public TerminalAiPromptMode Mode { get; set; } = TerminalAiPromptMode.Custom;

    public TerminalAiAccessMode AccessMode { get; set; } = TerminalAiAccessMode.Agent;

    public Guid HostId { get; set; }

    public string HostName { get; set; } = string.Empty;

    public string HostAddress { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string TerminalOutput { get; set; } = string.Empty;

    public string StructuredCommandOutput { get; set; } = string.Empty;

    public string Request { get; set; } = string.Empty;

    public string ActiveGoal { get; set; } = string.Empty;

    public bool AllowInternetResearch { get; set; }

    public bool SupportsManagedHomeLabOperations { get; set; }
}
