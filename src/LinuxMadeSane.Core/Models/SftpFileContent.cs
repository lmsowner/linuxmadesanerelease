// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record SftpFileContent(
    string FullPath,
    string Content,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    bool IsTruncated,
    string EncodingName = "utf-8");
