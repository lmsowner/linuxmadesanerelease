// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class ProtectedSecretEntity
{
    public string Reference { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string ProtectedValue { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
