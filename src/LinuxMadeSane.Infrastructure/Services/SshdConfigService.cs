using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshdConfigService(
    ILinuxCommandRunner commandRunner,
    ISftpBackupService backupService) : ISshdConfigService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<bool> SupportsDropInConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(SftpServerDefaults.ManagedDropInDirectory) ||
            !File.Exists(SftpServerDefaults.MainSshdConfigPath))
        {
            return false;
        }

        var mainConfig = await ReadTextOrEmptyAsync(SftpServerDefaults.MainSshdConfigPath, cancellationToken);
        return mainConfig.Contains("/etc/ssh/sshd_config.d/", StringComparison.Ordinal) ||
               mainConfig.Contains("/etc/ssh/sshd_config.d/*.conf", StringComparison.Ordinal);
    }

    public string BuildManagedConfig(string basePath)
    {
        var normalizedBasePath = NormalizePath(basePath);
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane.");
        builder.AppendLine("# Manual edits to this file will be overwritten.");
        builder.AppendLine();
        builder.AppendLine($"Match Group {SftpServerDefaults.ManagedUsersGroup}");
        builder.AppendLine($"    ChrootDirectory {normalizedBasePath}/%u");
        builder.AppendLine("    ForceCommand internal-sftp -d /files");
        builder.AppendLine($"    AuthorizedKeysFile {SftpServerDefaults.ManagedAuthorizedKeysDirectory}/%u");
        builder.AppendLine("    AllowTcpForwarding no");
        builder.AppendLine("    X11Forwarding no");
        builder.AppendLine("    PermitTunnel no");
        builder.AppendLine("    PermitTTY no");
        builder.AppendLine();
        builder.AppendLine($"Match Group {SftpServerDefaults.PasswordOnlyGroup}");
        builder.AppendLine("    PasswordAuthentication yes");
        builder.AppendLine("    PubkeyAuthentication no");
        builder.AppendLine();
        builder.AppendLine($"Match Group {SftpServerDefaults.PublicKeyOnlyGroup}");
        builder.AppendLine("    PasswordAuthentication no");
        builder.AppendLine("    PubkeyAuthentication yes");
        builder.AppendLine();
        builder.AppendLine($"Match Group {SftpServerDefaults.PublicKeyAndPasswordGroup}");
        builder.AppendLine("    AuthenticationMethods publickey,password");
        builder.AppendLine("    PasswordAuthentication yes");
        builder.AppendLine("    PubkeyAuthentication yes");
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public async Task<SftpValidationResult> ValidateConfigurationAsync(
        string proposedManagedConfig,
        bool useDropInConfiguration,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            var tempMainConfigPath = await StageValidationConfigurationAsync(
                tempDirectory,
                proposedManagedConfig,
                useDropInConfiguration,
                cancellationToken);

            var validate = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "bash",
                    ["-lc", $"if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t -f {SftpSystemCommandHelper.QuoteShellArgument(tempMainConfigPath)}; else sshd -t -f {SftpSystemCommandHelper.QuoteShellArgument(tempMainConfigPath)}; fi"],
                    true,
                    CommandTimeout,
                    "Validate proposed LMS SFTP sshd configuration"),
                dryRun: false,
                cancellationToken);

            return validate.ExitCode == 0
                ? new SftpValidationResult(true, "The proposed sshd configuration validates cleanly.", [], [], validate.CommandText)
                : new SftpValidationResult(
                    false,
                    SftpSystemCommandHelper.BuildFailureDetail(validate),
                    [SftpSystemCommandHelper.BuildFailureDetail(validate)],
                    [],
                    validate.CommandText);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public Task<IReadOnlyList<SftpConfigurationAction>> BuildApplyActionsAsync(
        string proposedManagedConfig,
        bool useDropInConfiguration,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<SftpConfigurationAction>();
        var configTargetPath = useDropInConfiguration
            ? SftpServerDefaults.ManagedDropInPath
            : SftpServerDefaults.MainSshdConfigPath;

        actions.Add(new SftpConfigurationAction(
            1,
            "Write LMS-managed sshd rules",
            useDropInConfiguration
                ? $"Install the LMS SFTP sshd drop-in at {configTargetPath}."
                : $"Update {configTargetPath} with the LMS SFTP managed block.",
            $"install -m 600 <temp> {configTargetPath}",
            true,
            configTargetPath,
            proposedManagedConfig));
        actions.Add(new SftpConfigurationAction(
            2,
            "Validate sshd",
            "Run sshd -t against the live configuration before reloading the service.",
            "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi",
            false,
            null,
            null));
        actions.Add(new SftpConfigurationAction(
            3,
            "Reload SSH service",
            "Reload the running SSH service without restarting active sessions.",
            "systemctl reload sshd || systemctl reload ssh",
            true,
            null,
            null));
        return Task.FromResult<IReadOnlyList<SftpConfigurationAction>>(actions);
    }

    public async Task<SftpApplyResult> ApplyManagedConfigurationAsync(
        string proposedManagedConfig,
        bool useDropInConfiguration,
        SftpBackupSnapshot backupSnapshot,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<OperationLogEntry>();

        try
        {
            if (useDropInConfiguration)
            {
                logs.AddRange(await EnsureDirectoryAsync(SftpServerDefaults.ManagedDropInDirectory, "755", "Ensure sshd drop-in directory exists", cancellationToken));
                await InstallManagedTextAsync(SftpServerDefaults.ManagedDropInPath, proposedManagedConfig, "600", logs, cancellationToken);
            }
            else
            {
                var mainConfig = await ReadTextOrEmptyAsync(SftpServerDefaults.MainSshdConfigPath, cancellationToken);
                var updatedMainConfig = UpsertManagedBlock(mainConfig, proposedManagedConfig);
                await InstallManagedTextAsync(SftpServerDefaults.MainSshdConfigPath, updatedMainConfig, "600", logs, cancellationToken);
            }

            var validate = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "bash",
                    ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi"],
                    true,
                    CommandTimeout,
                    "Validate live sshd configuration after LMS SFTP update"),
                dryRun: false,
                cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(validate, "Validate live sshd configuration"));

            if (validate.ExitCode != 0)
            {
                await backupService.RestoreAsync(backupSnapshot, cancellationToken);
                return new SftpApplyResult(
                    false,
                    $"sshd rejected the LMS SFTP configuration: {SftpSystemCommandHelper.BuildFailureDetail(validate)}",
                    logs,
                    new SftpValidationResult(false, SftpSystemCommandHelper.BuildFailureDetail(validate), [SftpSystemCommandHelper.BuildFailureDetail(validate)], [], validate.CommandText),
                    backupSnapshot,
                    true,
                    []);
            }

            var reload = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "bash",
                    ["-lc", "systemctl reload sshd || systemctl reload ssh"],
                    true,
                    CommandTimeout,
                    "Reload sshd after LMS SFTP update"),
                dryRun: false,
                cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(reload, "Reload SSH service"));

            if (reload.ExitCode != 0)
            {
                await backupService.RestoreAsync(backupSnapshot, cancellationToken);
                return new SftpApplyResult(
                    false,
                    $"SSH could not be reloaded after applying the LMS SFTP configuration: {SftpSystemCommandHelper.BuildFailureDetail(reload)}",
                    logs,
                    new SftpValidationResult(true, "The configuration validated, but the SSH service reload failed.", [], [SftpSystemCommandHelper.BuildFailureDetail(reload)], reload.CommandText),
                    backupSnapshot,
                    true,
                    [SftpSystemCommandHelper.BuildFailureDetail(reload)]);
            }

            return new SftpApplyResult(
                true,
                "Applied the LMS-managed SFTP sshd configuration.",
                logs,
                new SftpValidationResult(true, "The live sshd configuration validates cleanly.", [], [], validate.CommandText),
                backupSnapshot,
                false,
                []);
        }
        catch (Exception exception)
        {
            await backupService.RestoreAsync(backupSnapshot, cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapMessageLog($"Rolled back sshd files after failure: {exception.Message}"));
            return new SftpApplyResult(
                false,
                exception.Message,
                logs,
                new SftpValidationResult(false, exception.Message, [exception.Message], [], null),
                backupSnapshot,
                true,
                []);
        }
    }

    private async Task<string> StageValidationConfigurationAsync(
        string tempDirectory,
        string proposedManagedConfig,
        bool useDropInConfiguration,
        CancellationToken cancellationToken)
    {
        var tempMainConfigPath = Path.Combine(tempDirectory, "sshd_config");
        var liveMainConfig = await ReadTextOrEmptyAsync(SftpServerDefaults.MainSshdConfigPath, cancellationToken);

        if (!useDropInConfiguration)
        {
            await File.WriteAllTextAsync(tempMainConfigPath, UpsertManagedBlock(liveMainConfig, proposedManagedConfig), cancellationToken);
            return tempMainConfigPath;
        }

        var tempIncludeDirectory = Path.Combine(tempDirectory, "sshd_config.d");
        Directory.CreateDirectory(tempIncludeDirectory);

        if (Directory.Exists(SftpServerDefaults.ManagedDropInDirectory))
        {
            foreach (var existingConfig in Directory.GetFiles(SftpServerDefaults.ManagedDropInDirectory, "*.conf"))
            {
                var destination = Path.Combine(tempIncludeDirectory, Path.GetFileName(existingConfig));
                var existingConfigText = await ReadTextOrEmptyAsync(existingConfig, cancellationToken);
                await File.WriteAllTextAsync(destination, existingConfigText, cancellationToken);
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(tempIncludeDirectory, Path.GetFileName(SftpServerDefaults.ManagedDropInPath)),
            proposedManagedConfig,
            cancellationToken);

        var rewrittenMainConfig = liveMainConfig.Replace(
            "/etc/ssh/sshd_config.d",
            tempIncludeDirectory.Replace('\\', '/'),
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(tempMainConfigPath, rewrittenMainConfig, cancellationToken);
        return tempMainConfigPath;
    }

    private async Task<IReadOnlyList<OperationLogEntry>> EnsureDirectoryAsync(
        string path,
        string mode,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "install",
                ["-d", "-m", mode, path],
                true,
                CommandTimeout,
                description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not prepare '{path}' for SFTP SSH management: {SftpSystemCommandHelper.BuildFailureDetail(result)}");
        }

        return [SftpSystemCommandHelper.MapLog(result, description)];
    }

    private async Task InstallManagedTextAsync(
        string targetPath,
        string contents,
        string mode,
        List<OperationLogEntry> logs,
        CancellationToken cancellationToken)
    {
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempPath = Path.Combine(tempDirectory, Path.GetFileName(targetPath));
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);

            var install = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "install",
                    ["-m", mode, tempPath, targetPath],
                    true,
                    CommandTimeout,
                    $"Install LMS managed SFTP SSH configuration file {targetPath}"),
                dryRun: false,
                cancellationToken);
            logs.Add(SftpSystemCommandHelper.MapLog(install, $"Install SSH configuration file {targetPath}"));

            if (install.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane could not write '{targetPath}': {SftpSystemCommandHelper.BuildFailureDetail(install)}");
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static string UpsertManagedBlock(string mainConfig, string managedConfig)
    {
        var wrappedBlock = WrapManagedBlock(managedConfig);
        var startIndex = mainConfig.IndexOf(SftpServerDefaults.ManagedConfigStartMarker, StringComparison.Ordinal);
        var endIndex = mainConfig.IndexOf(SftpServerDefaults.ManagedConfigEndMarker, StringComparison.Ordinal);

        if (startIndex >= 0 && endIndex > startIndex)
        {
            var replaceLength = endIndex + SftpServerDefaults.ManagedConfigEndMarker.Length - startIndex;
            return mainConfig.Remove(startIndex, replaceLength).Insert(startIndex, wrappedBlock);
        }

        var builder = new StringBuilder(mainConfig.TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.Append(wrappedBlock);
        return builder.ToString();
    }

    private static string WrapManagedBlock(string managedConfig)
    {
        var builder = new StringBuilder();
        builder.AppendLine(SftpServerDefaults.ManagedConfigStartMarker);
        builder.Append(managedConfig.TrimEnd());
        builder.AppendLine();
        builder.AppendLine(SftpServerDefaults.ManagedConfigEndMarker);
        builder.AppendLine();
        return builder.ToString();
    }

    private async Task<string> ReadTextOrEmptyAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", $"cat {SftpSystemCommandHelper.QuoteShellArgument(path)}"],
                true,
                CommandTimeout,
                $"Read protected SSH configuration {path}"),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }

        throw new InvalidOperationException(
            $"Linux Made Sane could not read '{path}' while validating SSH configuration: {SftpSystemCommandHelper.BuildFailureDetail(result)}");
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return SftpServerDefaults.BasePath;
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized.TrimStart('/');
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lms-sftp-sshd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }
}
