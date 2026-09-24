// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class HomeLabStorageRoleEntity
{
    public string Role { get; set; } = string.Empty;
    public string HostPath { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
