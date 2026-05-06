using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiToolRegistry
{
    IReadOnlyList<AiToolDefinition> ListPublishedTools(
        AiChatThread thread,
        IReadOnlyList<AiAttachedServer> attachedServers);

    AiToolDefinition? FindTool(string toolName);
}
