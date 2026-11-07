// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

// Feature records belong in the existing database; secrets remain in ISecretStore.
public sealed class InfrastructureStateEntity
{
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
