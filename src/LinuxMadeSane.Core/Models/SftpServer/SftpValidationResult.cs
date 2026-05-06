namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpValidationResult(
    bool IsValid,
    string Summary,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? CommandText);
