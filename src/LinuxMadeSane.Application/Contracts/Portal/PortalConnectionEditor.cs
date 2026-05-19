// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Portal;

public sealed class PortalConnectionEditor
{
    public string PortalBaseUrl { get; set; } = string.Empty;
    public string InstanceDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}
