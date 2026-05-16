// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record SftpBinaryFileContent(
    string FullPath,
    byte[] ContentBytes,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    bool IsTruncated);
