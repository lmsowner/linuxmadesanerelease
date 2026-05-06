namespace LinuxMadeSane.Core.Models;

public sealed record SftpBinaryFileContent(
    string FullPath,
    byte[] ContentBytes,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    bool IsTruncated);
