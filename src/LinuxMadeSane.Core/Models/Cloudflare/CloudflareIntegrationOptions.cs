// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed class CloudflareIntegrationOptions
{
    [Required]
    public string ApiBaseUrl { get; set; } = "https://api.cloudflare.com/client/v4/";

    [Required]
    public string ManagedTunnelNamePrefix { get; set; } = "linux-made-sane";

    [Required]
    public string ManagedTag { get; set; } = "linux-made-sane";

    [Required]
    public string ManagedRecordComment { get; set; } = "Managed by Linux Made Sane";

    [Required]
    public string DefaultAccessSessionDuration { get; set; } = "24h";
}
