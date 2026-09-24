// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class FirewallAllowRuleEditor
{
    [Required]
    [StringLength(11)]
    public string Port { get; set; } = string.Empty;

    public FirewallProtocol Protocol { get; set; } = FirewallProtocol.Tcp;

    [StringLength(64)]
    public string Source { get; set; } = string.Empty;

    [StringLength(64)]
    public string Destination { get; set; } = string.Empty;

    [StringLength(80)]
    public string Comment { get; set; } = string.Empty;
}
