// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed class LocalAiSetupEditor
{
    public string SelectedModelId { get; set; } = "qwen2.5-coder:1.5b";
    public bool EnableSharing { get; set; }
}
