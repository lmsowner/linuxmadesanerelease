// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Portal;

public sealed class PortalConnectionEditor
{
    public string PortalBaseUrl { get; set; } = string.Empty;
    public string InstanceDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}
