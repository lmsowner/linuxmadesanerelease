// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

// Feature records belong in the existing database; secrets remain in ISecretStore.
public sealed class InfrastructureStateEntity
{
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
