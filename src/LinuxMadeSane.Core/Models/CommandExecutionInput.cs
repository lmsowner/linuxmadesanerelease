namespace LinuxMadeSane.Core.Models;

public sealed record CommandExecutionInput(
    string Content,
    bool IsSensitive);
