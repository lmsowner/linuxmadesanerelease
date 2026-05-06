using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSftpServerInspectionService(
    ISftpServerStore store,
    ISshdConfigService sshdConfigService,
    ILinuxCommandRunner commandRunner) : ISftpServerInspectionService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public async Task<SftpHostConfiguration> InspectAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        var users = await store.ListUsersAsync(cancellationToken);
        var warnings = new List<string>();

        var supportsDropIn = await sshdConfigService.SupportsDropInConfigurationAsync(cancellationToken);
        if (settings.PreferDropInConfiguration && !supportsDropIn)
        {
            warnings.Add("OpenSSH drop-in configuration is not available, so LMS will need to manage a marked block inside sshd_config.");
        }

        var versionResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -V 2>&1; elif command -v sshd >/dev/null 2>&1; then sshd -V 2>&1; else exit 127; fi"],
                false,
                CommandTimeout,
                "Inspect OpenSSH version")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var isOpenSshInstalled = versionResult.ExitCode == 0;
        var openSshVersion = isOpenSshInstalled
            ? SftpSystemCommandHelper.FirstNonEmptyLine(versionResult.StandardOutput, versionResult.StandardError) ?? string.Empty
            : string.Empty;

        var serviceName = ResolveSshServiceName();
        var serviceActiveResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "systemctl",
                ["is-active", serviceName],
                true,
                CommandTimeout,
                $"Inspect SSH service state ({serviceName})"),
            dryRun: false,
            cancellationToken);

        var validationResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi"],
                true,
                CommandTimeout,
                "Validate current sshd configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (validationResult.ExitCode != 0)
        {
            warnings.Add(SftpSystemCommandHelper.BuildFailureDetail(validationResult));
        }

        var missingGroups = new List<string>();
        foreach (var group in SftpServerDefaults.ManagedGroups)
        {
            var getent = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "getent",
                    ["group", group],
                    false,
                    CommandTimeout,
                    $"Inspect Linux group {group}"),
                dryRun: false,
                cancellationToken);

            if (getent.ExitCode != 0)
            {
                missingGroups.Add(group);
            }
        }

        var effectiveManagedConfigPath = settings.PreferDropInConfiguration && supportsDropIn
            ? settings.ManagedConfigPath
            : SftpServerDefaults.MainSshdConfigPath;
        var managedConfigPresent = effectiveManagedConfigPath == SftpServerDefaults.MainSshdConfigPath
            ? await MainConfigContainsManagedBlockAsync(cancellationToken)
            : File.Exists(effectiveManagedConfigPath);

        var isBasePathPresent = Directory.Exists(settings.BasePath);
        if (!isBasePathPresent)
        {
            warnings.Add($"The SFTP base path '{settings.BasePath}' does not exist yet.");
        }

        return new SftpHostConfiguration(
            settings,
            isOpenSshInstalled,
            openSshVersion,
            serviceActiveResult.ExitCode == 0,
            serviceName,
            supportsDropIn,
            effectiveManagedConfigPath,
            SftpServerDefaults.MainSshdConfigPath,
            managedConfigPresent,
            isBasePathPresent,
            validationResult.ExitCode == 0,
            validationResult.ExitCode == 0
                ? "The current sshd configuration validates cleanly."
                : SftpSystemCommandHelper.BuildFailureDetail(validationResult),
            users.Count,
            missingGroups,
            warnings);
    }

    private static string ResolveSshServiceName()
    {
        if (File.Exists("/lib/systemd/system/sshd.service") ||
            File.Exists("/etc/systemd/system/sshd.service"))
        {
            return "sshd";
        }

        return "ssh";
    }

    private static async Task<bool> MainConfigContainsManagedBlockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SftpServerDefaults.MainSshdConfigPath))
        {
            return false;
        }

        var mainConfig = await File.ReadAllTextAsync(SftpServerDefaults.MainSshdConfigPath, cancellationToken);
        return mainConfig.Contains(SftpServerDefaults.ManagedConfigStartMarker, StringComparison.Ordinal) &&
               mainConfig.Contains(SftpServerDefaults.ManagedConfigEndMarker, StringComparison.Ordinal);
    }
}
