namespace LinuxMadeSane.Core.Models;

public sealed record SftpWriteResult(
    string FullPath,
    long BytesWritten,
    DateTimeOffset CompletedAtUtc);
