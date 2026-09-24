// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed class LocalAiSetupEditor
{
    public string SelectedModelId { get; set; } = "qwen3.5:4b";
    public bool EnableSharing { get; set; }
}
