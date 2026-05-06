namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class TerminalAiConversationState
{
    public string ProviderKey { get; set; } = string.Empty;

    public string ProviderLabel { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string ProviderConversationReference { get; set; } = string.Empty;

    public string ProviderResponseId { get; set; } = string.Empty;

    public List<TerminalAiConversationEntry> Entries { get; set; } = [];
}
