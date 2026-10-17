// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class HomeLabStorageRoleEntity
{
    public string Role { get; set; } = string.Empty;
    public string HostPath { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
