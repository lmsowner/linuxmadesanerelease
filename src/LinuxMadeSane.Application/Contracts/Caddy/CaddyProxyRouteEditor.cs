// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed class CaddyProxyRouteEditor
{
    public Guid? Id { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string Hostname { get; set; } = string.Empty;

    [Required]
    [StringLength(512)]
    public string UpstreamUrl { get; set; } = string.Empty;

    [StringLength(320)]
    public string Description { get; set; } = string.Empty;

    public bool EnableTls { get; set; } = true;
}
