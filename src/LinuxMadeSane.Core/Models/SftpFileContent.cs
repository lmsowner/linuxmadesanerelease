// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record SftpFileContent(
    string FullPath,
    string Content,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    bool IsTruncated);
