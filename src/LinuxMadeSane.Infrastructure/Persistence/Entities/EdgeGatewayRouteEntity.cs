// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class EdgeGatewayRouteEntity
{
    public Guid Id { get; set; }
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public int TargetScheme { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; }
    public string TargetPathPrefix { get; set; } = string.Empty;
    public int AuthMode { get; set; }
    public string AllowedUsers { get; set; } = string.Empty;
    public string AllowedGroups { get; set; } = string.Empty;
    public bool AllowLanOnly { get; set; }
    public string AllowKnownIps { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LastTestStatus { get; set; }
    public string LastTestMessage { get; set; } = string.Empty;
}
