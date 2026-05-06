using System.Diagnostics;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalRemoteAccessSystemService(
    ILinuxCommandRunner commandRunner,
    ILogger<LocalRemoteAccessSystemService> logger) : IRemoteAccessSystemService
{
    private const string ManagedRootDirectory = "/etc/ssh/linuxmadesane";
    private const string ManagedAuthorizedKeysDirectory = "/etc/ssh/linuxmadesane/authorized_keys";
    private const string ManagedConfigDirectory = "/etc/ssh/sshd_config.d";
    private const string ManagedConfigPath = "/etc/ssh/sshd_config.d/90-linuxmadesane-remote-access.conf";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<bool> EnsureLocalAccountAsync(string linuxUsername, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = linuxUsername.Trim().ToLowerInvariant();
        var existing = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "id",
                ["-u", normalizedUsername],
                false,
                CommandTimeout,
                $"Check Linux account {normalizedUsername}"),
            dryRun: false,
            cancellationToken);

        if (existing.ExitCode == 0)
        {
            return false;
        }

        var create = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "useradd",
                ["-m", "-s", "/bin/bash", normalizedUsername],
                true,
                CommandTimeout,
                $"Create Linux account {normalizedUsername} for LMS remote access"),
            dryRun: false,
            cancellationToken);

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not create the local account '{normalizedUsername}': {BuildFailureDetail(create)}");
        }

        logger.LogInformation("Created local Linux account {LinuxUsername} for LMS remote access.", normalizedUsername);
        return true;
    }

    public async Task ApplySshAccessConfigurationAsync(IReadOnlyList<SecurityUser> users, CancellationToken cancellationToken = default)
    {
        var normalizedUsers = users
            .Where(user => !string.IsNullOrWhiteSpace(user.LinuxUsername))
            .OrderBy(user => user.LinuxUsername, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var user in normalizedUsers)
        {
            await EnsureLocalAccountAsync(user.LinuxUsername, cancellationToken);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"lms-remote-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await EnsureManagedDirectoriesAsync(cancellationToken);

            foreach (var user in normalizedUsers)
            {
                var tempAuthorizedKeysPath = Path.Combine(tempDirectory, $"{user.LinuxUsername}.authorized_keys");
                var authorizedKeysContent = string.IsNullOrWhiteSpace(user.AuthorizedKeyEntries)
                    ? string.Empty
                    : $"{user.AuthorizedKeyEntries.Trim()}{Environment.NewLine}";
                await File.WriteAllTextAsync(tempAuthorizedKeysPath, authorizedKeysContent, cancellationToken);

                var installAuthorizedKeys = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "install",
                        ["-m", "644", tempAuthorizedKeysPath, $"{ManagedAuthorizedKeysDirectory}/{user.LinuxUsername}"],
                        true,
                        CommandTimeout,
                        $"Install managed SSH key material for {user.LinuxUsername}"),
                    dryRun: false,
                    cancellationToken);

                if (installAuthorizedKeys.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Linux Made Sane could not install SSH key material for '{user.LinuxUsername}': {BuildFailureDetail(installAuthorizedKeys)}");
                }
            }

            var tempConfigPath = Path.Combine(tempDirectory, "90-linuxmadesane-remote-access.conf");
            await File.WriteAllTextAsync(tempConfigPath, BuildSshdConfig(normalizedUsers), cancellationToken);

            var installConfig = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "install",
                    ["-m", "600", tempConfigPath, ManagedConfigPath],
                    true,
                    CommandTimeout,
                    "Install LMS managed sshd remote access rules"),
                dryRun: false,
                cancellationToken);

            if (installConfig.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane could not install the sshd remote access rules: {BuildFailureDetail(installConfig)}");
            }

            var validate = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "bash",
                    ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi"],
                    true,
                    CommandTimeout,
                    "Validate sshd configuration after LMS remote access update"),
                dryRun: false,
                cancellationToken);

            if (validate.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane wrote remote access rules, but sshd rejected the configuration: {BuildFailureDetail(validate)}");
            }

            var reload = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "bash",
                    ["-lc", "systemctl reload sshd || systemctl reload ssh"],
                    true,
                    CommandTimeout,
                    "Reload sshd after LMS remote access update"),
                dryRun: false,
                cancellationToken);

            if (reload.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane updated the remote access rules, but sshd could not be reloaded: {BuildFailureDetail(reload)}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }

    public async Task ResetLocalPasswordAsync(string linuxUsername, string newPassword, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = linuxUsername.Trim().ToLowerInvariant();
        logger.LogInformation("Resetting local Linux password for LMS remote access user {LinuxUsername}.", normalizedUsername);

        var command = Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase)
            ? ("chpasswd", Array.Empty<string>())
            : ("sudo", new[] { "-n", "chpasswd" });

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Item1,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in command.Item2)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.StandardInput.WriteLineAsync($"{normalizedUsername}:{newPassword}");
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not update the password for '{normalizedUsername}': {FirstNonEmptyLine(stderr, stdout) ?? $"exit code {process.ExitCode}"}");
        }
    }

    public async Task DeleteLocalAccountAsync(string linuxUsername, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = linuxUsername.Trim().ToLowerInvariant();
        logger.LogInformation("Deleting LMS-managed local Linux account {LinuxUsername}.", normalizedUsername);

        var delete = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", $"userdel -r {QuoteShellArgument(normalizedUsername)} || userdel {QuoteShellArgument(normalizedUsername)}"],
                true,
                TimeSpan.FromSeconds(45),
                $"Delete Linux account {normalizedUsername}"),
            dryRun: false,
            cancellationToken);

        if (delete.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not delete the local account '{normalizedUsername}': {BuildFailureDetail(delete)}");
        }
    }

    private async Task EnsureManagedDirectoriesAsync(CancellationToken cancellationToken)
    {
        foreach (var directory in new[] { ManagedConfigDirectory, ManagedRootDirectory, ManagedAuthorizedKeysDirectory })
        {
            var create = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "install",
                    ["-d", "-m", "755", directory],
                    true,
                    CommandTimeout,
                    $"Ensure directory {directory} exists for LMS remote access"),
                dryRun: false,
                cancellationToken);

            if (create.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane could not prepare '{directory}' for remote access management: {BuildFailureDetail(create)}");
            }
        }
    }

    private static string BuildSshdConfig(IReadOnlyList<SecurityUser> users)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane.");
        builder.AppendLine("# Manual edits to this file will be overwritten.");
        builder.AppendLine();

        foreach (var user in users)
        {
            builder.AppendLine($"Match User {user.LinuxUsername}");

            if (!user.IsEnabled)
            {
                builder.AppendLine("    PasswordAuthentication no");
                builder.AppendLine("    KbdInteractiveAuthentication no");
                builder.AppendLine("    PubkeyAuthentication no");
                builder.AppendLine();
                continue;
            }

            switch (user.SshAuthenticationMode)
            {
                case RemoteAccessSshAuthenticationMode.Password:
                    builder.AppendLine("    PubkeyAuthentication no");
                    builder.AppendLine("    PasswordAuthentication yes");
                    builder.AppendLine("    KbdInteractiveAuthentication yes");
                    break;
                case RemoteAccessSshAuthenticationMode.PasswordAndKey:
                    builder.AppendLine($"    AuthorizedKeysFile {ManagedAuthorizedKeysDirectory}/%u");
                    builder.AppendLine("    PubkeyAuthentication yes");
                    builder.AppendLine("    PasswordAuthentication yes");
                    builder.AppendLine("    KbdInteractiveAuthentication yes");
                    builder.AppendLine("    AuthenticationMethods publickey,password publickey,keyboard-interactive");
                    break;
                case RemoteAccessSshAuthenticationMode.KeyOnly:
                    builder.AppendLine($"    AuthorizedKeysFile {ManagedAuthorizedKeysDirectory}/%u");
                    builder.AppendLine("    PubkeyAuthentication yes");
                    builder.AppendLine("    PasswordAuthentication no");
                    builder.AppendLine("    KbdInteractiveAuthentication no");
                    builder.AppendLine("    AuthenticationMethods publickey");
                    break;
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildFailureDetail(LinuxCommandResult result) =>
        FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}";

    private static string? FirstNonEmptyLine(params string[] values) =>
        values
            .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static string QuoteShellArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"') || value.Contains('\'')
            ? $"'{value.Replace("'", "'\"'\"'")}'"
            : value;
    }
}
