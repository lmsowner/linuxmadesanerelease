namespace LinuxMadeSane.Core.Models;

public sealed record SftpFileContent(
    string FullPath,
    string Content,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    bool IsTruncated);
