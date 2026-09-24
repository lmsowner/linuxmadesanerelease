// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class TrustedNetworkEntryEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string Label { get; set; } = string.Empty;

    [Required]
    public string AddressOrCidr { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool IsTrustedAccessEnabled { get; set; } = true;

    public bool IsAuthenticationEnabled { get; set; } = true;

    public NetworkAccessDeniedResponseMode DeniedResponseMode { get; set; } =
        NetworkAccessDeniedResponseMode.AccessDeniedPage;
}
