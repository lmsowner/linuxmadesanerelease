// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed class CaddyProxyRouteEditor
{
    public Guid? Id { get; set; }

    public CaddyProxyRouteKind Kind { get; set; } = CaddyProxyRouteKind.HostnameReverseProxy;

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [StringLength(255)]
    public string Hostname { get; set; } = string.Empty;

    [StringLength(512)]
    public string UpstreamUrl { get; set; } = string.Empty;

    [StringLength(320)]
    public string Description { get; set; } = string.Empty;

    public bool EnableTls { get; set; } = true;

    [StringLength(96)]
    public string SourceIp { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int SourcePort { get; set; } = 8080;

    [StringLength(255)]
    public string DestinationIp { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int DestinationPort { get; set; } = 80;

    public CaddyProxyTargetScheme DestinationScheme { get; set; } = CaddyProxyTargetScheme.Http;
}
