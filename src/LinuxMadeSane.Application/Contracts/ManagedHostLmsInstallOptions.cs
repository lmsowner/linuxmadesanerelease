// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed class ManagedHostLmsInstallOptions
{
    // Private, server-side input; never supplied to the AI or rendered in progress.
    public LinuxMadeSane.Core.Models.CommandExecutionInput? AdministratorInput { get; set; }
    public string InstallUrl { get; set; } = "https://www.linuxmadesane.com/install.sh";

    public bool StartService { get; set; } = true;

    public bool ConfigureLocalSshRunner { get; set; } = true;

    public bool EnableLocalSudo { get; set; } = true;

    public bool UpdateExistingInstall { get; set; } = true;

    public bool MarkAsLmsHostOnSuccess { get; set; } = true;
}
