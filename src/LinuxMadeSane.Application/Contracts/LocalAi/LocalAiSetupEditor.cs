// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed class LocalAiSetupEditor
{
    public string SelectedModelId { get; set; } = "qwen3.5:4b";
    public bool EnableSharing { get; set; }
}
