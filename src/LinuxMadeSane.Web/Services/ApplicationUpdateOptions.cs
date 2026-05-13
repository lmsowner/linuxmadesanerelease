namespace LinuxMadeSane.Web.Services;

public sealed class ApplicationUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string ManifestUrl { get; set; } = "https://www.linuxmadesane.com/api/downloads/manifest";

    public string InstallScriptUrl { get; set; } = "https://www.linuxmadesane.com/install.sh";

    public string Edition { get; set; } = "community";

    public string Rid { get; set; } = "linux-x64";

    public int CheckIntervalMinutes { get; set; } = 360;

    public bool InstallAutomatically { get; set; }

    public string UpdateCommand { get; set; } = string.Empty;

    public string UpdateHelperPath { get; set; } = "/usr/local/sbin/linux-made-sane-update";

    public int InstallTimeoutMinutes { get; set; } = 30;
}
