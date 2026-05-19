// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed class EdgeGatewayRouteEditor
{
    public Guid? Id { get; set; }
    public bool Enabled { get; set; } = true;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string Hostname { get; set; } = string.Empty;

    [Required]
    public string DomainName { get; set; } = string.Empty;

    public EdgeGatewayTargetScheme TargetScheme { get; set; } = EdgeGatewayTargetScheme.Http;

    [Required]
    public string TargetHost { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int TargetPort { get; set; } = 80;

    public string TargetPathPrefix { get; set; } = string.Empty;
    public EdgeGatewayAuthMode AuthMode { get; set; } = EdgeGatewayAuthMode.RequireMfa;
    public string AllowedUsers { get; set; } = string.Empty;
    public string AllowedGroups { get; set; } = string.Empty;
    public bool AllowLanOnly { get; set; }
    public string AllowKnownIps { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
