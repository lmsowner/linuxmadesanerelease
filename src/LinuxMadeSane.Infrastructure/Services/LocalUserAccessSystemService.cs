using System.Diagnostics;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalUserAccessSystemService(ILinuxCommandRunner commandRunner) : ILocalUserAccessSystemService
{
    private const string ManagedRootDirectory = "/etc/ssh/linuxmadesane/local-users";
    private const string ManagedAuthorizedKeysDirectory = "/etc/ssh/linuxmadesane/local-users/authorized_keys";
    private const string ManagedConfigDirectory = "/etc/ssh/sshd_config.d";
    private const string ManagedConfigPath = "/etc/ssh/sshd_config.d/91-linuxmadesane-local-users.conf";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task ApplyPoliciesAsync(IReadOnlyList<LocalUserAccessPolicy> policies, CancellationToken cancellationToken = default)
    {
        var normalizedPolicies = policies
            .Where(policy => !string.IsNullOrWhiteSpace(policy.UserName))
            .OrderBy(policy => policy.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tempDirectory = CreateTemporaryDirectory();

        try
        {
            await EnsureManagedDirectoriesAsync(cancellationToken);
            await ClearManagedAuthorizedKeysDirectoryAsync(cancellationToken);

            foreach (var policy in normalizedPolicies)
            {
                if (string.IsNullOrWhiteSpace(policy.AuthorizedKeyEntries))
                {
                    continue;
                }

                await InstallManagedTextAsync(
                    tempDirectory,
                    $"{policy.UserName}.authorized_keys",
                    $"{policy.AuthorizedKeyEntries.Trim()}{Environment.NewLine}",
                    "644",
                    $"{ManagedAuthorizedKeysDirectory}/{policy.UserName}",
                    $"Install managed SSH key material for local user {policy.UserName}",
                    $"Linux Made Sane could not install SSH key material for '{policy.UserName}'",
                    cancellationToken);
            }

            await InstallManagedTextAsync(
                tempDirectory,
                "91-linuxmadesane-local-users.conf",
                BuildSshdConfig(normalizedPolicies),
                "600",
                ManagedConfigPath,
                "Install LMS managed sshd local user rules",
                "Linux Made Sane could not install the sshd local user rules",
                cancellationToken);

            await ValidateAndReloadSshdAsync(cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not stage the local-user SSH access update. {exception.Message}",
                exception);
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

    private async Task InstallManagedTextAsync(
        string tempDirectory,
        string temporaryFileName,
        string content,
        string mode,
        string destinationPath,
        string description,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(tempDirectory, temporaryFileName);
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);

        var installResult = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "install",
                ["-m", mode, tempPath, destinationPath],
                true,
                CommandTimeout,
                description),
            dryRun: false,
            cancellationToken);

        if (installResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"{failurePrefix}: {BuildFailureDetail(installResult)}");
        }
    }

    public async Task ResetPasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
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
        await process.StandardInput.WriteLineAsync($"{normalizedUserName}:{newPassword}");
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
                $"Linux Made Sane could not update the password for '{normalizedUserName}': {FirstNonEmptyLine(stderr, stdout) ?? $"exit code {process.ExitCode}"}");
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
                    $"Ensure directory {directory} exists for LMS local user SSH access"),
                dryRun: false,
                cancellationToken);

            if (create.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Linux Made Sane could not prepare '{directory}' for local user SSH access management: {BuildFailureDetail(create)}");
            }
        }
    }

    private async Task ClearManagedAuthorizedKeysDirectoryAsync(CancellationToken cancellationToken)
    {
        var clear = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", $"rm -f {QuoteShellArgument(ManagedAuthorizedKeysDirectory)}/*"],
                true,
                CommandTimeout,
                "Clear LMS managed local-user SSH key directory"),
            dryRun: false,
            cancellationToken);

        if (clear.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane could not refresh the local-user SSH key directory: {BuildFailureDetail(clear)}");
        }
    }

    private async Task ValidateAndReloadSshdAsync(CancellationToken cancellationToken)
    {
        var validate = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi"],
                true,
                CommandTimeout,
                "Validate sshd configuration after LMS local-user SSH update"),
            dryRun: false,
            cancellationToken);

        if (validate.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane wrote local-user SSH rules, but sshd rejected the configuration: {BuildFailureDetail(validate)}");
        }

        var reload = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", "systemctl reload sshd || systemctl reload ssh"],
                true,
                CommandTimeout,
                "Reload sshd after LMS local-user SSH update"),
            dryRun: false,
            cancellationToken);

        if (reload.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Linux Made Sane updated the local-user SSH rules, but sshd could not be reloaded: {BuildFailureDetail(reload)}");
        }
    }

    private static string BuildSshdConfig(IReadOnlyList<LocalUserAccessPolicy> policies)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane.");
        builder.AppendLine("# Manual edits to this file will be overwritten.");
        builder.AppendLine();

        foreach (var policy in policies)
        {
            builder.AppendLine($"Match User {policy.UserName}");

            switch (policy.SshAuthenticationMode)
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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "linuxmadesane", "local-user-access", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
