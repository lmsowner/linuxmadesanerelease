namespace LinuxMadeSane.Application.Contracts;

public sealed class ManagedHostLmsUninstallOptions
{
    public string InstallUrl { get; set; } = "https://www.linuxmadesane.com/install.sh";

    public bool RemoveData { get; set; }

    public bool MarkAsSshHostOnSuccess { get; set; } = true;
}
