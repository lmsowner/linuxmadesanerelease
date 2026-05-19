// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class EdgeGatewaySettingsEntity
{
    public int Id { get; set; }
    public string GatewaySubdomain { get; set; } = "relay";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
