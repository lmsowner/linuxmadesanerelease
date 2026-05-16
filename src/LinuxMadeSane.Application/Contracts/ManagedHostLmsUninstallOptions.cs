// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed class ManagedHostLmsUninstallOptions
{
    public string InstallUrl { get; set; } = "https://www.linuxmadesane.com/install.sh";

    public bool RemoveData { get; set; }

    public bool MarkAsSshHostOnSuccess { get; set; } = true;
}
