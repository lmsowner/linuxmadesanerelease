namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolExecutionContext(
    AiToolInvocation Invocation,
    AiChatThread Thread,
    IReadOnlyList<AiAttachedServer> AttachedServers);
