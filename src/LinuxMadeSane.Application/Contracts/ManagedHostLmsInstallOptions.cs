namespace LinuxMadeSane.Application.Contracts;

public sealed class ManagedHostLmsInstallOptions
{
    public string InstallUrl { get; set; } = "https://www.linuxmadesane.com/install.sh";

    public bool StartService { get; set; } = true;

    public bool ConfigureLocalSshRunner { get; set; } = true;

    public bool EnableLocalSudo { get; set; } = true;

    public bool UpdateExistingInstall { get; set; } = true;

    public bool MarkAsLmsHostOnSuccess { get; set; } = true;
}
