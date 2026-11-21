// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileTransferProgress(
    long BytesTransferred,
    long? TotalBytes,
    string? Detail = null);
