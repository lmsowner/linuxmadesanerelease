// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed class ManagedHostLmsUninstallOptions
{
    public string InstallUrl { get; set; } = "https://www.linuxmadesane.com/install.sh";

    public bool RemoveData { get; set; }

    public bool MarkAsSshHostOnSuccess { get; set; } = true;
}
