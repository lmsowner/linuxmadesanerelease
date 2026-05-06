namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpConfigurationAction(
    int Order,
    string Title,
    string Summary,
    string CommandText,
    bool MutatesSystem,
    string? TargetPath,
    string? ProposedContent);
