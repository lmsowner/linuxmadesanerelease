namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed class LocalAiSetupEditor
{
    public string SelectedModelId { get; set; } = "qwen2.5-coder:1.5b";
    public bool EnableSharing { get; set; }
}
