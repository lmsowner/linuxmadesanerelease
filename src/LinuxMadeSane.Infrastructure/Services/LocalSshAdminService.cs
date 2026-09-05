// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSshAdminService : ISshAdminService, IHostedService, IDisposable
{
    public static readonly TimeSpan DefaultTrialDuration = TimeSpan.FromMinutes(2);

    internal const string ManagedConfigurationPath = "/etc/ssh/sshd_config.d/00-linuxmadesane-security.conf";
    internal const string KeyOnboardingConfigurationPath = "/etc/ssh/sshd_config.d/01-linuxmadesane-key-onboarding.conf";
    internal const string AuthenticatorGroup = "lms-ssh-2fa";
    internal const string PamSshdPath = "/etc/pam.d/sshd";

    private const string MainConfigurationPath = "/etc/ssh/sshd_config";
    private const string PamBlockStart = "# BEGIN Linux Made Sane SSH authenticator";
    private const string PamBlockEnd = "# END Linux Made Sane SSH authenticator";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VerifiedLoginLifetime = TimeSpan.FromMinutes(15);
    private static readonly Regex UserNamePattern = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex FingerprintPattern = new("^SHA256:[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILinuxCommandRunner commandRunner;
    private readonly SshAdminStorageSettings storageSettings;
    private readonly ILogger<LocalSshAdminService> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan trialDuration;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Dictionary<string, VerifiedKeyLogin> verifiedKeyLogins = new(StringComparer.Ordinal);
    private SshTrialRecovery? activeRecovery;
    private CancellationTokenSource? trialCancellation;
    private Task? trialMonitor;
    private bool disposed;

    public LocalSshAdminService(
        ILinuxCommandRunner commandRunner,
        SshAdminStorageSettings storageSettings,
        ILogger<LocalSshAdminService> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? trialDuration = null)
    {
        this.commandRunner = commandRunner;
        this.storageSettings = storageSettings;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.trialDuration = trialDuration ?? DefaultTrialDuration;
    }

    private string RecoveryPath => Path.Combine(storageSettings.DirectoryPath, "pending-ssh-hardening-trial.json");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var recovery = await ReadRecoveryAsync(cancellationToken);
            if (recovery is null)
            {
                return;
            }

            logger.LogWarning("Recovering unfinished SSH hardening trial {TrialId}", recovery.Trial.Id);
            try
            {
                await RestoreManagedConfigurationAsync(recovery, cancellationToken);
                DeleteRecoveryFile();
            }
            catch (Exception exception)
            {
                activeRecovery = recovery;
                logger.LogError(
                    exception,
                    "Could not recover unfinished SSH hardening trial {TrialId}; LMS will remain available for manual recovery",
                    recovery.Trial.Id);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        trialCancellation?.Cancel();
        await operationGate.WaitAsync(CancellationToken.None);
        try
        {
            if (activeRecovery is null)
            {
                return;
            }

            await RestoreManagedConfigurationAsync(activeRecovery, CancellationToken.None);
            DeleteRecoveryFile();
            activeRecovery = null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not restore the active SSH hardening trial while LMS was stopping");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshAdminOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            RemoveExpiredVerifications();
            return await ReadOverviewAsync(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshHardeningPlan> PreviewHardeningAsync(
        SshHardeningEditor editor,
        string? verifiedUserName,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var overview = await ReadOverviewAsync(cancellationToken);
            return BuildHardeningPlan(editor, overview, verifiedUserName);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshAdminOperationResult> StartHardeningTrialAsync(
        SshHardeningEditor editor,
        string? verifiedUserName,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            if (activeRecovery is not null)
            {
                throw new InvalidOperationException("Keep or revert the current SSH test before making another SSH change.");
            }

            var overview = await ReadOverviewAsync(cancellationToken);
            var plan = BuildHardeningPlan(editor, overview, verifiedUserName);
            if (!plan.CanApply)
            {
                throw new InvalidOperationException(plan.Warnings.FirstOrDefault() ?? "The SSH hardening plan is not safe to apply.");
            }

            Directory.CreateDirectory(storageSettings.DirectoryPath);
            var oldConfiguration = await ReadPrivilegedTextOrNullAsync(ManagedConfigurationPath, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var trial = new SshAdminTrialViewModel(
                Guid.NewGuid(),
                "SSH hardening test",
                "The previous SSH settings will return automatically unless you keep this change.",
                now,
                now.Add(trialDuration));
            var recovery = new SshTrialRecovery(trial, oldConfiguration is not null, oldConfiguration ?? string.Empty);
            await PersistRecoveryAsync(recovery, cancellationToken);

            try
            {
                await InstallManagedConfigurationAsync(plan.GeneratedConfiguration, cancellationToken);
                await ValidateAndReloadAsync(cancellationToken);
            }
            catch
            {
                await RestoreManagedConfigurationAsync(recovery, CancellationToken.None);
                DeleteRecoveryFile();
                throw;
            }

            activeRecovery = recovery;
            StartTrialMonitor(recovery);
            return new SshAdminOperationResult(
                true,
                $"The proposed SSH settings are active for {trialDuration.TotalMinutes:0} minutes. Open a new SSH connection now, then keep the change.",
                trial);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task ConfirmTrialAsync(Guid trialId, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            RequireActiveTrial(trialId);
            trialCancellation?.Cancel();
            DeleteRecoveryFile();
            activeRecovery = null;
            logger.LogInformation("Kept SSH hardening trial {TrialId}", trialId);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RevertTrialAsync(Guid trialId, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var recovery = RequireActiveTrial(trialId);
            trialCancellation?.Cancel();
            await RestoreManagedConfigurationAsync(recovery, cancellationToken);
            DeleteRecoveryFile();
            activeRecovery = null;
            logger.LogInformation("Reverted SSH hardening trial {TrialId}", trialId);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshPublicKeyInstallResult> InstallPublicKeyAsync(
        string userName,
        string publicKey,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var user = await GetRequiredInteractiveUserAsync(userName, cancellationToken);
            var normalizedKey = NormalizePublicKey(publicKey);
            var tempDirectory = CreateTemporaryDirectory();
            try
            {
                var keyPath = Path.Combine(tempDirectory, "key.pub");
                await File.WriteAllTextAsync(keyPath, normalizedKey + Environment.NewLine, cancellationToken);
                var fingerprintResult = await RunAsync(
                    "ssh-keygen",
                    ["-lf", keyPath],
                    false,
                    "Validate SSH public key",
                    cancellationToken,
                    optional: true);
                if (fingerprintResult.ExitCode != 0)
                {
                    throw new InvalidOperationException("That public key is not valid OpenSSH public-key text. Paste the single line from the .pub file, never the private key.");
                }

                var fingerprint = ParseFingerprint(fingerprintResult.StandardOutput);
                var userSshContext = await ResolveUserSshContextAsync(user.UserName, user.HomeDirectory, cancellationToken);
                var authorizedKeysPath = userSshContext.AuthorizedKeysPath;
                var sshDirectory = Path.GetDirectoryName(authorizedKeysPath)
                                   ?? throw new InvalidOperationException("OpenSSH returned an invalid authorized_keys path.");
                var isHomeKeyFile = IsPathInsideHome(authorizedKeysPath, user.HomeDirectory);

                EnsureSafeHomePath(user.HomeDirectory);
                EnsureSafeAuthorizedKeysPath(authorizedKeysPath, user.HomeDirectory);
                LinuxCommandResult ensureDirectory;
                if (isHomeKeyFile)
                {
                    MakeTemporaryFileReadableByTargetUser(tempDirectory, keyPath);
                    ensureDirectory = await RunAsync(
                        "runuser",
                        ["-u", user.UserName, "--", "install", "-d", "-m", "700", sshDirectory],
                        true,
                        $"Prepare .ssh for {user.UserName}",
                        cancellationToken);
                }
                else
                {
                    ensureDirectory = await RunAsync(
                        "install",
                        ["-d", "-m", "755", sshDirectory],
                        true,
                        $"Prepare managed authorized_keys directory for {user.UserName}",
                        cancellationToken);
                }

                EnsureSuccess(ensureDirectory, $"LMS could not prepare {sshDirectory}");

                var script = "set -eu\n" +
                             $"auth={QuoteShellArgument(authorizedKeysPath)}\n" +
                             $"key={QuoteShellArgument(keyPath)}\n" +
                             "touch \"$auth\"\n" +
                             (isHomeKeyFile
                                 ? "chmod 600 \"$auth\"\n"
                                 : "chown root:root \"$auth\"\nchmod 644 \"$auth\"\n") +
                             "if grep -Fqx -- \"$(cat \"$key\")\" \"$auth\"; then exit 10; fi\n" +
                             "if [ -s \"$auth\" ] && [ \"$(tail -c 1 \"$auth\" | wc -l)\" -eq 0 ]; then printf '\\n' >> \"$auth\"; fi\n" +
                             "cat \"$key\" >> \"$auth\"\n";
                var installResult = await RunAsync(
                    isHomeKeyFile ? "runuser" : "bash",
                    isHomeKeyFile
                        ? ["-u", user.UserName, "--", "bash", "-lc", script]
                        : ["-lc", script],
                    true,
                    $"Install public key for {user.UserName}",
                    cancellationToken);
                if (installResult.ExitCode is not 0 and not 10)
                {
                    EnsureSuccess(installResult, $"LMS could not install the public key for '{user.UserName}'");
                }

                var enabledKeyOnboarding = false;
                if (!userSshContext.PublicKeyAuthentication)
                {
                    await EnablePublicKeyOnboardingAsync(user.UserName, authorizedKeysPath, cancellationToken);
                    enabledKeyOnboarding = true;
                }

                var alreadyInstalled = installResult.ExitCode == 10;
                return new SshPublicKeyInstallResult(
                    true,
                    (alreadyInstalled
                        ? $"That key was already installed for {user.UserName}."
                        : $"Public key installed for {user.UserName}. The private key remains only on your client device.") +
                    (enabledKeyOnboarding
                        ? " LMS also enabled a password-or-key onboarding rule for this account so you can test the key before removing passwords."
                        : " Test a fresh login with its matching private key."),
                    fingerprint,
                    alreadyInstalled);
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshKeyLoginVerificationResult> VerifyRecentPublicKeyLoginAsync(
        string userName,
        string fingerprintSha256,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var user = await GetRequiredInteractiveUserAsync(userName, cancellationToken);
            var fingerprint = NormalizeFingerprint(fingerprintSha256);
            var earliest = timeProvider.GetUtcNow().Subtract(TimeSpan.FromMinutes(30));
            var since = sinceUtc < earliest ? earliest : sinceUtc;
            var result = await RunAsync(
                "journalctl",
                ["--since", $"@{since.ToUnixTimeSeconds()}", "-u", "ssh.service", "-u", "sshd.service", "--no-pager", "-o", "short-iso"],
                true,
                $"Check recent public-key SSH logins for {user.UserName}",
                cancellationToken);
            if (result.ExitCode != 0)
            {
                return new SshKeyLoginVerificationResult(false, "LMS could not read the SSH authentication journal. Keep password access enabled and check the server logs manually.");
            }

            var acceptedLine = SplitLines(result.StandardOutput)
                .LastOrDefault(line =>
                    line.Contains($"Accepted publickey for {user.UserName} ", StringComparison.Ordinal) &&
                    line.Contains(fingerprint, StringComparison.Ordinal));
            if (acceptedLine is null)
            {
                return new SshKeyLoginVerificationResult(
                    false,
                    "No fresh login using that key is visible yet. Leave this page open, connect from a second terminal, then check again.");
            }

            var acceptedAt = timeProvider.GetUtcNow();
            verifiedKeyLogins[user.UserName] = new VerifiedKeyLogin(fingerprint, acceptedAt);
            return new SshKeyLoginVerificationResult(
                true,
                $"Verified: OpenSSH accepted this public key for {user.UserName}. Key-only hardening is now unlocked for 15 minutes.",
                acceptedAt);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshAdminOperationResult> RemovePublicKeyAsync(
        string userName,
        string fingerprintSha256,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var user = await GetRequiredInteractiveUserAsync(userName, cancellationToken);
            var fingerprint = NormalizeFingerprint(fingerprintSha256);
            var key = user.AuthorizedKeys.SingleOrDefault(item => item.FingerprintSha256 == fingerprint)
                      ?? throw new InvalidOperationException("That public key is no longer installed for the selected user.");
            var userSshContext = await ResolveUserSshContextAsync(user.UserName, user.HomeDirectory, cancellationToken);
            if (user.AuthorizedKeys.Count == 1 && !userSshContext.PasswordAuthentication)
            {
                throw new InvalidOperationException(
                    "LMS will not remove this user's last public key while password login is blocked. Add and test a replacement key, or temporarily restore password access first.");
            }

            var existing = await ReadPrivilegedTextOrNullAsync(userSshContext.AuthorizedKeysPath, cancellationToken)
                           ?? throw new InvalidOperationException("The authorized_keys file is no longer available.");
            var existingLines = existing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var retainedLines = existingLines
                .Where(line => !TryParseAuthorizedKey(line, out var parsed) || parsed.FingerprintSha256 != fingerprint)
                .ToArray();
            if (retainedLines.Length == existingLines.Length)
            {
                throw new InvalidOperationException("That public key is no longer present in the authorized_keys file.");
            }

            await InstallAuthorizedKeysContentsAsync(
                user,
                userSshContext.AuthorizedKeysPath,
                retainedLines.Length == 0 ? string.Empty : string.Join(Environment.NewLine, retainedLines) + Environment.NewLine,
                cancellationToken);
            verifiedKeyLogins.Remove(user.UserName);
            return new SshAdminOperationResult(true, $"Removed {key.Algorithm} key {key.FingerprintSha256} from {user.UserName}.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshTotpEnrollment> BeginAuthenticatorEnrollmentAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var user = await GetRequiredInteractiveUserAsync(userName, cancellationToken);
            if (user.AuthorizedKeyCount == 0)
            {
                throw new InvalidOperationException("Install and test a public key for this user before adding an authenticator code.");
            }

            var secret = TotpAuthenticator.GenerateSecret();
            var issuer = $"LMS SSH ({Environment.MachineName})";
            return new SshTotpEnrollment(
                user.UserName,
                secret,
                TotpAuthenticator.FormatManualEntryKey(secret),
                TotpAuthenticator.BuildOtpUri(user.UserName, secret, issuer),
                GenerateRecoveryCodes());
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshAdminOperationResult> EnableAuthenticatorAsync(
        string userName,
        string secret,
        string verificationCode,
        IReadOnlyList<string> recoveryCodes,
        string fingerprintSha256,
        DateTimeOffset keyLoginVerifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var user = await GetRequiredInteractiveUserAsync(userName, cancellationToken);
            var fingerprint = NormalizeFingerprint(fingerprintSha256);
            if (!HasFreshVerifiedLogin(user.UserName, fingerprint) ||
                keyLoginVerifiedAtUtc < timeProvider.GetUtcNow().Subtract(VerifiedLoginLifetime))
            {
                throw new InvalidOperationException("Test a fresh public-key login before enabling SSH authenticator protection.");
            }

            if (!TotpAuthenticator.ValidateCode(secret, verificationCode))
            {
                throw new InvalidOperationException("That authenticator code was not valid. Check the device time and enter the current six-digit code.");
            }

            if (await ReadPrivilegedTextOrNullAsync(ManagedConfigurationPath, cancellationToken) is null)
            {
                throw new InvalidOperationException("Apply the LMS-managed SSH hardening configuration before enabling SSH authenticator protection.");
            }

            var currentOverview = await ReadOverviewAsync(cancellationToken);
            if (!currentOverview.EffectiveSettings.UsePam)
            {
                throw new InvalidOperationException("Enable PAM in the LMS hardening policy before adding an SSH authenticator.");
            }

            await EnsureAuthenticatorModuleInstalledAsync(cancellationToken);
            await EnsureAuthenticatorPamPolicyAsync(cancellationToken);
            await EnsureAuthenticatorGroupAsync(cancellationToken);
            await InstallAuthenticatorSecretAsync(user, secret, recoveryCodes, cancellationToken);
            await ValidateAndReloadAsync(cancellationToken);

            var addGroup = await RunAsync(
                "usermod",
                ["-a", "-G", AuthenticatorGroup, user.UserName],
                true,
                $"Require an SSH authenticator for {user.UserName}",
                cancellationToken);
            EnsureSuccess(addGroup, $"LMS could not enable SSH authenticator protection for '{user.UserName}'");

            return new SshAdminOperationResult(
                true,
                $"SSH now requires both the tested private key and an authenticator code for {user.UserName}. Open a second connection before closing the current one.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SshAdminOperationResult> DisableAuthenticatorAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var user = await GetRequiredInteractiveUserAsync(userName, cancellationToken);
            var removeGroup = await RunAsync(
                "gpasswd",
                ["-d", user.UserName, AuthenticatorGroup],
                true,
                $"Stop requiring an SSH authenticator for {user.UserName}",
                cancellationToken);
            if (removeGroup.ExitCode != 0 &&
                !removeGroup.StandardError.Contains("not a member", StringComparison.OrdinalIgnoreCase))
            {
                EnsureSuccess(removeGroup, $"LMS could not disable SSH authenticator protection for '{user.UserName}'");
            }

            var secretPath = Path.Combine(user.HomeDirectory, ".google_authenticator");
            EnsureSafeHomePath(user.HomeDirectory);
            await RunAsync("rm", ["-f", secretPath], true, $"Remove SSH authenticator secret for {user.UserName}", cancellationToken);
            await CleanupAuthenticatorPolicyIfUnusedAsync(cancellationToken);
            return new SshAdminOperationResult(true, $"Authenticator codes are no longer required for {user.UserName}. The server's normal SSH policy now applies.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal static SshEffectiveSettings ParseEffectiveSettings(string output)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(output))
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!values.TryGetValue(key, out var entries))
            {
                entries = [];
                values[key] = entries;
            }

            entries.Add(value);
        }

        var ports = ReadAll("port")
            .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0)
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .ToArray();
        if (ports.Length == 0)
        {
            ports = [22];
        }

        return new SshEffectiveSettings(
            ports,
            ReadAll("listenaddress"),
            Read("permitrootlogin", "unknown"),
            ReadBool("passwordauthentication", true),
            ReadBool("pubkeyauthentication", true),
            ReadBool("kbdinteractiveauthentication", false),
            ReadBool("usepam", true),
            ReadBool("permitemptypasswords", false),
            ReadBool("x11forwarding", false),
            ReadBool("allowagentforwarding", true),
            Read("allowtcpforwarding", "yes") is not "no",
            ReadInt("maxauthtries", 6),
            ParseDurationSeconds(Read("logingracetime", "120"), 120));

        string Read(string key, string fallback) =>
            values.TryGetValue(key, out var entries) && entries.Count > 0 ? entries[0] : fallback;

        IReadOnlyList<string> ReadAll(string key) =>
            values.TryGetValue(key, out var entries) ? entries : [];

        bool ReadBool(string key, bool fallback) => Read(key, fallback ? "yes" : "no") == "yes";

        int ReadInt(string key, int fallback) =>
            int.TryParse(Read(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
    }

    internal static string BuildManagedConfiguration(SshHardeningEditor editor)
    {
        var normalized = NormalizeEditor(editor);
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane SSH Admin.");
        builder.AppendLine("# Use LMS Security to change this file; manual edits will be overwritten.");
        builder.AppendLine($"Port {normalized.Port}");
        builder.AppendLine($"PermitRootLogin {(normalized.PermitRootLogin ? "yes" : "no")}");
        builder.AppendLine($"PasswordAuthentication {YesNo(normalized.PasswordAuthentication)}");
        builder.AppendLine($"PubkeyAuthentication {YesNo(normalized.PublicKeyAuthentication)}");
        builder.AppendLine($"KbdInteractiveAuthentication {YesNo(normalized.KeyboardInteractiveAuthentication)}");
        builder.AppendLine($"UsePAM {YesNo(normalized.UsePam)}");
        builder.AppendLine("PermitEmptyPasswords no");
        builder.AppendLine($"X11Forwarding {YesNo(normalized.X11Forwarding)}");
        builder.AppendLine($"AllowAgentForwarding {YesNo(normalized.AllowAgentForwarding)}");
        builder.AppendLine($"AllowTcpForwarding {YesNo(normalized.AllowTcpForwarding)}");
        builder.AppendLine($"MaxAuthTries {normalized.MaxAuthTries}");
        builder.AppendLine($"LoginGraceTime {normalized.LoginGraceTimeSeconds}");
        builder.AppendLine();
        builder.AppendLine($"Match Group {AuthenticatorGroup}");
        builder.AppendLine("    PubkeyAuthentication yes");
        builder.AppendLine("    PasswordAuthentication no");
        builder.AppendLine("    KbdInteractiveAuthentication yes");
        builder.AppendLine("    AuthenticationMethods publickey,keyboard-interactive:pam");
        builder.AppendLine("Match all");
        if (!normalized.PasswordAuthentication)
        {
            builder.AppendLine();
            builder.AppendLine("# Enforce key-only login before later per-user LMS and SFTP rules are read.");
            builder.AppendLine("Match User *");
            builder.AppendLine("    PubkeyAuthentication yes");
            builder.AppendLine("    PasswordAuthentication no");
            builder.AppendLine("    KbdInteractiveAuthentication no");
            builder.AppendLine("    AuthenticationMethods publickey");
            if (!normalized.PermitRootLogin)
            {
                builder.AppendLine("    PermitRootLogin no");
            }

            builder.AppendLine("Match all");
        }

        return builder.ToString();

        static string YesNo(bool value) => value ? "yes" : "no";
    }

    internal static string UpsertPamBlock(string existing)
    {
        var block = string.Join(Environment.NewLine,
        [
            PamBlockStart,
            $"auth [success=2 default=ignore] pam_succeed_if.so user notingroup {AuthenticatorGroup}",
            "auth required pam_google_authenticator.so",
            "auth sufficient pam_permit.so",
            PamBlockEnd
        ]);
        var withoutOldBlock = RemoveMarkedBlock(existing, PamBlockStart, PamBlockEnd).TrimStart('\r', '\n');
        return block + Environment.NewLine + withoutOldBlock;
    }

    private async Task<SshAdminOverview> ReadOverviewAsync(CancellationToken cancellationToken)
    {
        var version = await RunAsync(
            "bash",
            ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -V 2>&1; elif command -v sshd >/dev/null 2>&1; then sshd -V 2>&1; else exit 127; fi"],
            false,
            "Inspect OpenSSH version",
            cancellationToken,
            optional: true);
        var installed = version.ExitCode == 0;
        var serviceName = ResolveServiceName();
        var service = await RunAsync("systemctl", ["is-active", serviceName], true, "Inspect SSH service state", cancellationToken);
        var validation = installed
            ? await RunAsync("bash", ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi"], true, "Validate current SSH configuration", cancellationToken)
            : version;
        var effectiveResult = installed
            ? await RunAsync("bash", ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -T; else sshd -T; fi"], true, "Read effective SSH settings", cancellationToken)
            : version;
        var effective = ParseEffectiveSettings(effectiveResult.ExitCode == 0 ? effectiveResult.StandardOutput : string.Empty);
        var hostAddressesResult = await RunAsync("hostname", ["--all-ip-addresses"], false, "Read host addresses", cancellationToken, optional: true);
        var addresses = hostAddressesResult.StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(address => address is not "127.0.0.1" and not "::1")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var users = await ReadInteractiveUsersAsync(cancellationToken);
        var hostKeys = await ReadHostKeysAsync(cancellationToken);
        var moduleInstalled = await IsAuthenticatorModuleInstalledAsync(cancellationToken);
        var supportsDropIn = await SupportsDropInConfigurationAsync(cancellationToken);
        var managedConfiguration = supportsDropIn
            ? await ReadPrivilegedTextOrNullAsync(ManagedConfigurationPath, cancellationToken)
            : null;

        return new SshAdminOverview(
            installed,
            FirstNonEmptyLine(version.StandardOutput, version.StandardError) ?? "Not installed",
            service.ExitCode == 0,
            serviceName,
            validation.ExitCode == 0,
            validation.ExitCode == 0 ? "OpenSSH accepts the current configuration." : BuildFailureDetail(validation),
            Environment.MachineName,
            addresses,
            effective,
            hostKeys,
            users,
            BuildAssessments(effective, installed, service.ExitCode == 0, validation.ExitCode == 0),
            managedConfiguration is not null,
            ManagedConfigurationPath,
            moduleInstalled,
            CanInstallAuthenticatorModule(),
            activeRecovery?.Trial);
    }

    private SshHardeningPlan BuildHardeningPlan(
        SshHardeningEditor editor,
        SshAdminOverview overview,
        string? verifiedUserName)
    {
        var normalized = NormalizeEditor(editor);
        var changes = new List<string>();
        var warnings = new List<string>();
        var hasBlockingIssue = false;
        var safetyChecks = new List<string>
        {
            "LMS writes a separate managed drop-in; it does not rewrite /etc/ssh/sshd_config.",
            "OpenSSH validates the complete configuration before LMS reloads the service.",
            $"Existing SSH sessions remain open and the old settings return after {trialDuration.TotalMinutes:0} minutes unless you keep the change."
        };

        AddChange(overview.EffectiveSettings.Ports.FirstOrDefault(22) != normalized.Port, $"Listen on TCP port {normalized.Port}.");
        AddChange(IsRootLoginAllowed(overview.EffectiveSettings.PermitRootLogin) != normalized.PermitRootLogin,
            normalized.PermitRootLogin ? "Allow direct root login." : "Block direct root login.");
        AddChange(overview.EffectiveSettings.PasswordAuthentication != normalized.PasswordAuthentication,
            normalized.PasswordAuthentication ? "Allow password login." : "Reject password login.");
        AddChange(overview.EffectiveSettings.PublicKeyAuthentication != normalized.PublicKeyAuthentication,
            normalized.PublicKeyAuthentication ? "Allow public-key login." : "Reject public-key login.");
        AddChange(overview.EffectiveSettings.KeyboardInteractiveAuthentication != normalized.KeyboardInteractiveAuthentication,
            normalized.KeyboardInteractiveAuthentication ? "Allow keyboard-interactive authentication." : "Disable keyboard-interactive authentication except for LMS authenticator users.");
        AddChange(overview.EffectiveSettings.UsePam != normalized.UsePam,
            normalized.UsePam ? "Keep PAM available for account checks and optional authenticator codes." : "Disable PAM integration.");
        AddChange(overview.EffectiveSettings.X11Forwarding != normalized.X11Forwarding,
            normalized.X11Forwarding ? "Allow X11 forwarding." : "Disable X11 forwarding.");
        AddChange(overview.EffectiveSettings.AllowAgentForwarding != normalized.AllowAgentForwarding,
            normalized.AllowAgentForwarding ? "Allow SSH agent forwarding." : "Disable SSH agent forwarding.");
        AddChange(overview.EffectiveSettings.AllowTcpForwarding != normalized.AllowTcpForwarding,
            normalized.AllowTcpForwarding ? "Allow SSH tunnels and port forwarding." : "Disable SSH tunnels and port forwarding.");
        AddChange(overview.EffectiveSettings.MaxAuthTries != normalized.MaxAuthTries,
            $"Allow {normalized.MaxAuthTries} authentication attempts per connection.");
        AddChange(overview.EffectiveSettings.LoginGraceTimeSeconds != normalized.LoginGraceTimeSeconds,
            $"Close incomplete logins after {normalized.LoginGraceTimeSeconds} seconds.");

        var requiresVerifiedKey = !normalized.PasswordAuthentication;
        if (requiresVerifiedKey)
        {
            var verified = !string.IsNullOrWhiteSpace(verifiedUserName) && HasFreshVerifiedLogin(verifiedUserName, null);
            if (!verified)
            {
                AddBlocker("Password login can only be disabled after LMS sees a fresh successful public-key login for the selected user.");
            }

            warnings.Add("Password removal is server-wide: accounts without a working key will no longer be able to sign in. The selected verified admin key is the recovery path for this test.");
        }

        if (!normalized.PasswordAuthentication && !normalized.PublicKeyAuthentication)
        {
            AddBlocker("Password and public-key login cannot both be disabled.");
        }

        if (!overview.IsOpenSshInstalled)
        {
            AddBlocker("OpenSSH Server is not installed on this machine.");
        }
        else if (!overview.IsConfigurationValid)
        {
            AddBlocker("Fix the current invalid OpenSSH configuration before applying a hardening plan.");
        }

        if (!overview.IsServiceActive)
        {
            AddBlocker("The SSH service is stopped. Start it before using a live hardening test.");
        }

        if (!File.Exists(MainConfigurationPath) || !Directory.Exists(Path.GetDirectoryName(ManagedConfigurationPath)!))
        {
            AddBlocker("This OpenSSH installation does not expose the standard sshd drop-in directory. LMS will not rewrite the main configuration file automatically.");
        }

        if (normalized.Port != overview.EffectiveSettings.Ports.FirstOrDefault(22))
        {
            warnings.Add("A port change may also need a matching firewall, router, cloud security-group, and systemd ssh.socket change. The automatic rollback protects the sshd file, but test the new port before keeping it.");
        }

        if (normalized.PermitRootLogin)
        {
            warnings.Add("Direct root login is usually unnecessary. Prefer a named account plus sudo so administrative actions have an identity.");
        }

        if (!normalized.UsePam && normalized.KeyboardInteractiveAuthentication)
        {
            AddBlocker("Keyboard-interactive authentication needs PAM on the Linux distributions supported by this guided setup.");
        }

        if (!normalized.UsePam && overview.Users.Any(user => user.IsAuthenticatorRequired))
        {
            AddBlocker("PAM cannot be disabled while an SSH user is protected by an authenticator code. Disable authenticator protection for those users first.");
        }

        return new SshHardeningPlan(
            BuildManagedConfiguration(normalized),
            changes.Count == 0 ? ["Re-apply and validate the current LMS-managed policy."] : changes,
            safetyChecks,
            warnings,
            requiresVerifiedKey,
            !hasBlockingIssue);

        void AddChange(bool condition, string description)
        {
            if (condition)
            {
                changes.Add(description);
            }
        }

        void AddBlocker(string warning)
        {
            warnings.Add(warning);
            hasBlockingIssue = true;
        }
    }

    private async Task<IReadOnlyList<SshLocalUserViewModel>> ReadInteractiveUsersAsync(CancellationToken cancellationToken)
    {
        var passwd = await RunAsync("getent", ["passwd"], false, "Read local Linux users for SSH administration", cancellationToken);
        if (passwd.ExitCode != 0)
        {
            return [];
        }

        var users = new List<SshLocalUserViewModel>();
        foreach (var line in SplitLines(passwd.StandardOutput))
        {
            var parts = line.Split(':');
            if (parts.Length < 7 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            {
                continue;
            }

            var userName = parts[0];
            var home = parts[5];
            var shell = parts[6];
            if ((uid < 1000 && userName != "root") || uid == 65534 || !IsInteractiveShell(shell) || !IsSafeHomePath(home))
            {
                continue;
            }

            var status = await RunAsync("passwd", ["-S", userName], true, $"Inspect account state for {userName}", cancellationToken);
            var statusParts = status.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var locked = statusParts.Length > 1 && statusParts[1] is "L" or "LK";
            var userSshContext = await ResolveUserSshContextAsync(userName, home, cancellationToken);
            var authorizedKeysPath = userSshContext.AuthorizedKeysPath;
            var secretPath = Path.Combine(home, ".google_authenticator");
            var inspectScript =
                $"totp=0; [ -s {QuoteShellArgument(secretPath)} ] && totp=1; " +
                $"group=0; id -nG {QuoteShellArgument(userName)} | tr ' ' '\\n' | grep -Fxq {QuoteShellArgument(AuthenticatorGroup)} && group=1 || true; " +
                "printf '%s|%s\\n' \"$totp\" \"$group\"";
            var inspection = await RunAsync("bash", ["-lc", inspectScript], true, $"Inspect SSH readiness for {userName}", cancellationToken);
            var values = (FirstNonEmptyLine(inspection.StandardOutput) ?? "0|0").Split('|');
            var authorizedKeys = await ReadAuthorizedKeysAsync(authorizedKeysPath, cancellationToken);
            users.Add(new SshLocalUserViewModel(
                userName,
                string.IsNullOrWhiteSpace(parts[4]) ? userName : parts[4].Split(',')[0],
                home,
                shell,
                locked,
                authorizedKeys.Count,
                authorizedKeys,
                userSshContext.PublicKeyAuthentication,
                userSshContext.AuthenticationMethods,
                values.Length > 0 && values[0] == "1",
                values.Length > 1 && values[1] == "1"));
        }

        return users.OrderBy(user => user.UserName == Environment.UserName ? 0 : user.UserName == "root" ? 2 : 1)
            .ThenBy(user => user.UserName, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<SshHostKeyViewModel>> ReadHostKeysAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "bash",
            ["-lc", "for key in /etc/ssh/ssh_host_*_key.pub; do [ -f \"$key\" ] || continue; ssh-keygen -lf \"$key\"; done"],
            false,
            "Read SSH server identity fingerprints",
            cancellationToken,
            optional: true);
        if (result.ExitCode != 0)
        {
            return [];
        }

        return SplitLines(result.StandardOutput)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 4 && int.TryParse(parts[0], out _) && parts[1].StartsWith("SHA256:", StringComparison.Ordinal))
            .Select(parts => new SshHostKeyViewModel(
                parts[^1].Trim('(', ')'),
                parts[1].TrimEnd('='),
                int.Parse(parts[0], CultureInfo.InvariantCulture)))
            .OrderBy(key => key.Algorithm, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<UserSshContext> ResolveUserSshContextAsync(
        string userName,
        string homeDirectory,
        CancellationToken cancellationToken)
    {
        var effective = await RunAsync(
            "bash",
            ["-lc", $"if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -T -C user={QuoteShellArgument(userName)},host=localhost,addr=127.0.0.1; else sshd -T -C user={QuoteShellArgument(userName)},host=localhost,addr=127.0.0.1; fi"],
            true,
            $"Resolve effective authorized_keys location for {userName}",
            cancellationToken);
        var effectiveLines = SplitLines(effective.StandardOutput);
        var configured = effectiveLines
            .FirstOrDefault(line => line.StartsWith("authorizedkeysfile ", StringComparison.OrdinalIgnoreCase))?
            ["authorizedkeysfile ".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(path => !path.Equals("none", StringComparison.OrdinalIgnoreCase));
        configured ??= ".ssh/authorized_keys";

        var uidResult = await RunAsync("id", ["-u", userName], false, $"Resolve numeric user ID for {userName}", cancellationToken);
        var numericUserId = uidResult.ExitCode == 0 ? FirstNonEmptyLine(uidResult.StandardOutput) : null;
        const string literalPercentToken = "__LMS_LITERAL_PERCENT__";
        var expanded = configured
            .Replace("%%", literalPercentToken, StringComparison.Ordinal)
            .Replace("%h", homeDirectory, StringComparison.Ordinal)
            .Replace("%u", userName, StringComparison.Ordinal)
            .Replace("%U", numericUserId ?? "%U", StringComparison.Ordinal)
            .Replace(literalPercentToken, "%", StringComparison.Ordinal);
        if (expanded.Contains('%'))
        {
            throw new InvalidOperationException($"OpenSSH uses an authorized_keys path token that LMS cannot safely expand for '{userName}'.");
        }

        var path = Path.IsPathRooted(expanded) ? expanded : Path.Combine(homeDirectory, expanded);
        var publicKeyAuthentication = ReadEffectiveValue("pubkeyauthentication", "yes") == "yes";
        var passwordAuthentication = ReadEffectiveValue("passwordauthentication", "yes") == "yes";
        var authenticationMethods = ReadEffectiveValue("authenticationmethods", "any");
        return new UserSshContext(Path.GetFullPath(path), publicKeyAuthentication, passwordAuthentication, authenticationMethods);

        string ReadEffectiveValue(string key, string fallback) =>
            effectiveLines.FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase))?
                [(key.Length + 1)..]
                .Trim() ?? fallback;
    }

    private async Task<IReadOnlyList<SshAuthorizedKeyViewModel>> ReadAuthorizedKeysAsync(
        string authorizedKeysPath,
        CancellationToken cancellationToken)
    {
        var contents = await ReadPrivilegedTextOrNullAsync(authorizedKeysPath, cancellationToken);
        if (contents is null)
        {
            return [];
        }

        return contents
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => TryParseAuthorizedKey(line, out var key) ? key : null)
            .OfType<SshAuthorizedKeyViewModel>()
            .DistinctBy(key => key.FingerprintSha256, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool TryParseAuthorizedKey(string line, out SshAuthorizedKeyViewModel key)
    {
        key = new SshAuthorizedKeyViewModel(string.Empty, string.Empty, string.Empty);
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = Array.FindIndex(parts, IsPublicKeyType);
        if (typeIndex < 0 || typeIndex + 1 >= parts.Length)
        {
            return false;
        }

        try
        {
            var blob = Convert.FromBase64String(parts[typeIndex + 1]);
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
            var comment = typeIndex + 2 < parts.Length ? string.Join(' ', parts[(typeIndex + 2)..]) : "No comment";
            key = new SshAuthorizedKeyViewModel(parts[typeIndex], fingerprint, comment);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task InstallAuthorizedKeysContentsAsync(
        SshLocalUserViewModel user,
        string targetPath,
        string contents,
        CancellationToken cancellationToken)
    {
        EnsureSafeAuthorizedKeysPath(targetPath, user.HomeDirectory);
        var isHomeKeyFile = IsPathInsideHome(targetPath, user.HomeDirectory);
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempPath = Path.Combine(tempDirectory, "authorized_keys");
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
            if (isHomeKeyFile)
            {
                MakeTemporaryFileReadableByTargetUser(tempDirectory, tempPath);
            }

            var result = await RunAsync(
                isHomeKeyFile ? "runuser" : "install",
                isHomeKeyFile
                    ? ["-u", user.UserName, "--", "install", "-m", "600", tempPath, targetPath]
                    : ["-m", "644", "-o", "root", "-g", "root", tempPath, targetPath],
                true,
                $"Update authorized_keys for {user.UserName}",
                cancellationToken);
            EnsureSuccess(result, $"LMS could not update the public keys for '{user.UserName}'");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task EnablePublicKeyOnboardingAsync(
        string userName,
        string authorizedKeysPath,
        CancellationToken cancellationToken)
    {
        if (authorizedKeysPath.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("LMS cannot create a safe key-onboarding rule for an authorized_keys path containing whitespace.");
        }

        var oldConfiguration = await ReadPrivilegedTextOrNullAsync(KeyOnboardingConfigurationPath, cancellationToken);
        var entries = ParseKeyOnboardingEntries(oldConfiguration ?? string.Empty).ToDictionary(entry => entry.UserName, StringComparer.Ordinal);
        entries[userName] = new KeyOnboardingEntry(userName, authorizedKeysPath);
        var configuration = BuildKeyOnboardingConfiguration(entries.Values);

        try
        {
            await InstallTextAsync(KeyOnboardingConfigurationPath, configuration, "Install temporary password-or-key onboarding policy", cancellationToken);
            await ValidateAndReloadAsync(cancellationToken);
        }
        catch
        {
            if (oldConfiguration is null)
            {
                await RunAsync("rm", ["-f", KeyOnboardingConfigurationPath], true, "Remove failed SSH key-onboarding policy", CancellationToken.None);
            }
            else
            {
                await InstallTextAsync(KeyOnboardingConfigurationPath, oldConfiguration, "Restore SSH key-onboarding policy", CancellationToken.None);
            }

            await ValidateAndReloadAsync(CancellationToken.None);
            throw;
        }
    }

    internal static string BuildKeyOnboardingConfiguration(IEnumerable<KeyOnboardingEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane SSH Admin.");
        builder.AppendLine("# These users may use a password or their new key while onboarding.");
        foreach (var entry in entries.OrderBy(entry => entry.UserName, StringComparer.Ordinal))
        {
            var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.AuthorizedKeysPath));
            builder.AppendLine();
            builder.AppendLine($"# LMS-KEY-ONBOARDING {entry.UserName} {encodedPath}");
            builder.AppendLine($"Match User {entry.UserName}");
            builder.AppendLine($"    AuthorizedKeysFile {entry.AuthorizedKeysPath}");
            builder.AppendLine("    PubkeyAuthentication yes");
            builder.AppendLine("    PasswordAuthentication yes");
            builder.AppendLine("    KbdInteractiveAuthentication yes");
            builder.AppendLine("    AuthenticationMethods any");
            builder.AppendLine("Match all");
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<KeyOnboardingEntry> ParseKeyOnboardingEntries(string configuration)
    {
        var entries = new List<KeyOnboardingEntry>();
        foreach (var line in SplitLines(configuration))
        {
            const string marker = "# LMS-KEY-ONBOARDING ";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line[marker.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !UserNamePattern.IsMatch(parts[0]))
            {
                continue;
            }

            try
            {
                entries.Add(new KeyOnboardingEntry(parts[0], Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]))));
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("The LMS SSH key-onboarding file contains an invalid managed entry.");
            }
        }

        return entries;
    }

    private static IReadOnlyList<SshSettingAssessment> BuildAssessments(
        SshEffectiveSettings settings,
        bool installed,
        bool active,
        bool valid)
    {
        if (!installed)
        {
            return [new SshSettingAssessment("OpenSSH Server", "Not installed", "There is no SSH server to harden on this host.", SshAssessmentTone.Neutral)];
        }

        return
        [
            new("SSH service", active ? "Running" : "Stopped", active ? "Remote SSH connections can reach the service when the network and firewall allow them." : "No new SSH connections are being accepted by the systemd service.", active ? SshAssessmentTone.Good : SshAssessmentTone.Neutral),
            new("Configuration", valid ? "Valid" : "Needs repair", valid ? "OpenSSH accepts the complete active configuration." : "OpenSSH reports a configuration error. Fix this before reloading it.", valid ? SshAssessmentTone.Good : SshAssessmentTone.Attention),
            new("Public-key login", settings.PublicKeyAuthentication ? "Allowed" : "Disabled", settings.PublicKeyAuthentication ? "Clients may prove possession of a private key instead of sending a reusable password." : "Enable this before moving users away from passwords.", settings.PublicKeyAuthentication ? SshAssessmentTone.Good : SshAssessmentTone.Attention),
            new("Password login", settings.PasswordAuthentication ? "Allowed" : "Blocked", settings.PasswordAuthentication ? "Useful during key setup, but exposed accounts still depend on password strength. Test a key before turning this off." : "Network password guessing cannot produce an SSH login.", settings.PasswordAuthentication ? SshAssessmentTone.Neutral : SshAssessmentTone.Good),
            new("Direct root login", IsRootLoginAllowed(settings.PermitRootLogin) ? settings.PermitRootLogin : "Blocked", IsRootLoginAllowed(settings.PermitRootLogin) ? "Prefer a named account plus sudo so activity is attributable." : "Administrators must sign in as a named user before elevating privileges.", IsRootLoginAllowed(settings.PermitRootLogin) ? SshAssessmentTone.Attention : SshAssessmentTone.Good),
            new("Empty passwords", settings.PermitEmptyPasswords ? "Allowed" : "Blocked", settings.PermitEmptyPasswords ? "Accounts without a password could be accepted when password login is enabled." : "OpenSSH rejects accounts with an empty password.", settings.PermitEmptyPasswords ? SshAssessmentTone.Attention : SshAssessmentTone.Good),
            new("Login attempts", settings.MaxAuthTries.ToString(CultureInfo.InvariantCulture), settings.MaxAuthTries <= 4 ? "A connection gets only a small number of authentication attempts." : "Reducing this to three or four makes repeated guessing less efficient.", settings.MaxAuthTries <= 4 ? SshAssessmentTone.Good : SshAssessmentTone.Neutral),
            new("SSH tunnels", settings.AllowTcpForwarding ? "Allowed" : "Blocked", settings.AllowTcpForwarding ? "Needed for SSH tunnels and some administration tools. Disable it only if you do not use them." : "This server will not relay TCP connections through SSH.", SshAssessmentTone.Neutral)
        ];
    }

    private async Task<SshLocalUserViewModel> GetRequiredInteractiveUserAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = NormalizeUserName(userName);
        var users = await ReadInteractiveUsersAsync(cancellationToken);
        return users.SingleOrDefault(user => user.UserName == normalized)
               ?? throw new InvalidOperationException($"'{normalized}' is not an interactive local Linux user on this host.");
    }

    private async Task InstallManagedConfigurationAsync(string configuration, CancellationToken cancellationToken)
    {
        await InstallTextAsync(
            ManagedConfigurationPath,
            configuration,
            "Install LMS-managed SSH hardening policy",
            cancellationToken);
    }

    private async Task InstallTextAsync(
        string targetPath,
        string configuration,
        string description,
        CancellationToken cancellationToken,
        string mode = "600")
    {
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempPath = Path.Combine(tempDirectory, Path.GetFileName(targetPath));
            await File.WriteAllTextAsync(tempPath, configuration, cancellationToken);
            var install = await RunAsync(
                "install",
                ["-m", mode, tempPath, targetPath],
                true,
                description,
                cancellationToken);
            EnsureSuccess(install, $"LMS could not install {targetPath}");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task RestoreManagedConfigurationAsync(SshTrialRecovery recovery, CancellationToken cancellationToken)
    {
        if (recovery.HadManagedConfiguration)
        {
            await InstallManagedConfigurationAsync(recovery.ManagedConfiguration, cancellationToken);
        }
        else
        {
            var remove = await RunAsync("rm", ["-f", ManagedConfigurationPath], true, "Remove trial SSH hardening policy", cancellationToken);
            EnsureSuccess(remove, "LMS could not remove the trial SSH hardening policy");
        }

        await ValidateAndReloadAsync(cancellationToken);
    }

    private async Task ValidateAndReloadAsync(CancellationToken cancellationToken)
    {
        var validate = await RunAsync(
            "bash",
            ["-lc", "if [ -x /usr/sbin/sshd ]; then /usr/sbin/sshd -t; else sshd -t; fi"],
            true,
            "Validate SSH configuration",
            cancellationToken);
        EnsureSuccess(validate, "OpenSSH rejected the proposed configuration");

        var reload = await RunAsync(
            "bash",
            ["-lc", "systemctl reload sshd || systemctl reload ssh"],
            true,
            "Reload SSH without closing active sessions",
            cancellationToken);
        EnsureSuccess(reload, "The SSH service could not be reloaded");
    }

    private async Task<bool> SupportsDropInConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(MainConfigurationPath) || !Directory.Exists(Path.GetDirectoryName(ManagedConfigurationPath)!))
        {
            return false;
        }

        var main = await ReadPrivilegedTextOrNullAsync(MainConfigurationPath, cancellationToken);
        return main?.Contains("/etc/ssh/sshd_config.d/", StringComparison.Ordinal) == true;
    }

    private async Task<string?> ReadPrivilegedTextOrNullAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync("cat", [path], true, $"Read {path}", cancellationToken, optional: true);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private async Task EnsureAuthenticatorModuleInstalledAsync(CancellationToken cancellationToken)
    {
        if (await IsAuthenticatorModuleInstalledAsync(cancellationToken))
        {
            return;
        }

        var packageCommand = ResolveAuthenticatorPackageCommand()
            ?? throw new InvalidOperationException("LMS could not identify a supported package manager for the Google Authenticator PAM module.");
        foreach (var command in packageCommand)
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(command.FileName, command.Arguments, true, PackageTimeout, command.Description),
                dryRun: false,
                cancellationToken);
            EnsureSuccess(result, "The SSH authenticator PAM module could not be installed");
        }

        if (!await IsAuthenticatorModuleInstalledAsync(cancellationToken))
        {
            throw new InvalidOperationException("The package command completed, but pam_google_authenticator.so is still unavailable.");
        }
    }

    private async Task<bool> IsAuthenticatorModuleInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "bash",
            ["-lc", "find /lib /usr/lib -type f -name pam_google_authenticator.so -print -quit 2>/dev/null | grep -q ."],
            false,
            "Check SSH authenticator PAM module",
            cancellationToken,
            optional: true);
        return result.ExitCode == 0;
    }

    private static bool CanInstallAuthenticatorModule() => ResolveAuthenticatorPackageCommand() is not null;

    private static IReadOnlyList<PackageCommand>? ResolveAuthenticatorPackageCommand()
    {
        if (File.Exists("/usr/bin/apt-get"))
        {
            return
            [
                new("apt-get", ["update"], "Refresh package metadata for SSH authenticator support"),
                new("apt-get", ["install", "--yes", "libpam-google-authenticator"], "Install SSH authenticator PAM support")
            ];
        }

        if (File.Exists("/usr/bin/dnf"))
        {
            return [new("dnf", ["install", "-y", "google-authenticator"], "Install SSH authenticator PAM support")];
        }

        if (File.Exists("/usr/bin/yum"))
        {
            return [new("yum", ["install", "-y", "google-authenticator"], "Install SSH authenticator PAM support")];
        }

        if (File.Exists("/usr/bin/zypper"))
        {
            return [new("zypper", ["--non-interactive", "install", "google-authenticator-libpam"], "Install SSH authenticator PAM support")];
        }

        return null;
    }

    private async Task EnsureAuthenticatorPamPolicyAsync(CancellationToken cancellationToken)
    {
        var existing = await ReadPrivilegedTextOrNullAsync(PamSshdPath, cancellationToken)
                       ?? throw new InvalidOperationException($"LMS could not read {PamSshdPath}.");
        var updated = UpsertPamBlock(existing);
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempPath = Path.Combine(tempDirectory, "sshd.pam");
            await File.WriteAllTextAsync(tempPath, updated, cancellationToken);
            var install = await RunAsync("install", ["-m", "644", tempPath, PamSshdPath], true, "Install SSH authenticator PAM policy", cancellationToken);
            EnsureSuccess(install, $"LMS could not update {PamSshdPath}");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task EnsureAuthenticatorGroupAsync(CancellationToken cancellationToken)
    {
        var inspect = await RunAsync("getent", ["group", AuthenticatorGroup], false, "Check SSH authenticator group", cancellationToken);
        if (inspect.ExitCode == 0)
        {
            return;
        }

        var create = await RunAsync("groupadd", ["--system", AuthenticatorGroup], true, "Create SSH authenticator group", cancellationToken);
        EnsureSuccess(create, "LMS could not create the SSH authenticator group");
    }

    private async Task CleanupAuthenticatorPolicyIfUnusedAsync(CancellationToken cancellationToken)
    {
        var group = await RunAsync("getent", ["group", AuthenticatorGroup], false, "Check remaining SSH authenticator users", cancellationToken);
        if (group.ExitCode != 0)
        {
            return;
        }

        var fields = (FirstNonEmptyLine(group.StandardOutput) ?? string.Empty).Split(':');
        if (fields.Length >= 4 && !string.IsNullOrWhiteSpace(fields[3]))
        {
            return;
        }

        var existingPam = await ReadPrivilegedTextOrNullAsync(PamSshdPath, cancellationToken);
        if (existingPam?.Contains(PamBlockStart, StringComparison.Ordinal) == true)
        {
            var cleanedPam = RemoveMarkedBlock(existingPam, PamBlockStart, PamBlockEnd).TrimStart('\r', '\n');
            await InstallTextAsync(PamSshdPath, cleanedPam, "Remove unused LMS SSH authenticator PAM policy", cancellationToken, "644");
        }

        var removeGroup = await RunAsync("groupdel", [AuthenticatorGroup], true, "Remove unused SSH authenticator group", cancellationToken);
        if (removeGroup.ExitCode != 0)
        {
            logger.LogWarning("Could not remove the now-unused SSH authenticator group: {Failure}", BuildFailureDetail(removeGroup));
        }
    }

    private async Task InstallAuthenticatorSecretAsync(
        SshLocalUserViewModel user,
        string secret,
        IReadOnlyList<string> recoveryCodes,
        CancellationToken cancellationToken)
    {
        var normalizedSecret = new string(secret.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalizedSecret.Length < 16)
        {
            throw new InvalidOperationException("The authenticator secret is not valid.");
        }

        var normalizedRecoveryCodes = recoveryCodes
            .Select(code => new string((code ?? string.Empty).Where(char.IsDigit).ToArray()))
            .Where(code => code.Length == 8)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedRecoveryCodes.Length < 5)
        {
            throw new InvalidOperationException("The SSH authenticator recovery codes are incomplete. Start enrollment again.");
        }

        var lines = new List<string>
        {
            normalizedSecret,
            "\" RATE_LIMIT 3 30",
            "\" WINDOW_SIZE 3",
            "\" DISALLOW_REUSE",
            "\" TOTP_AUTH"
        };
        lines.AddRange(normalizedRecoveryCodes);
        lines.Add(string.Empty);
        var contents = string.Join(Environment.NewLine, lines);
        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempPath = Path.Combine(tempDirectory, "google_authenticator");
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
            var target = Path.Combine(user.HomeDirectory, ".google_authenticator");
            EnsureSafeHomePath(user.HomeDirectory);
            MakeTemporaryFileReadableByTargetUser(tempDirectory, tempPath);
            var install = await RunAsync(
                "runuser",
                ["-u", user.UserName, "--", "install", "-m", "600", tempPath, target],
                true,
                $"Install SSH authenticator secret for {user.UserName}",
                cancellationToken);
            EnsureSuccess(install, $"LMS could not install the authenticator secret for '{user.UserName}'");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private void StartTrialMonitor(SshTrialRecovery recovery)
    {
        trialCancellation?.Cancel();
        trialCancellation?.Dispose();
        trialCancellation = new CancellationTokenSource();
        trialMonitor = MonitorTrialAsync(recovery, trialCancellation.Token);
    }

    private async Task MonitorTrialAsync(SshTrialRecovery recovery, CancellationToken cancellationToken)
    {
        try
        {
            var delay = recovery.Trial.ExpiresAtUtc - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }

            await operationGate.WaitAsync(cancellationToken);
            try
            {
                if (activeRecovery?.Trial.Id != recovery.Trial.Id)
                {
                    return;
                }

                await RestoreManagedConfigurationAsync(recovery, CancellationToken.None);
                DeleteRecoveryFile();
                activeRecovery = null;
                logger.LogWarning("Automatically reverted unconfirmed SSH hardening trial {TrialId}", recovery.Trial.Id);
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not automatically revert SSH hardening trial {TrialId}", recovery.Trial.Id);
        }
    }

    private async Task PersistRecoveryAsync(SshTrialRecovery recovery, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageSettings.DirectoryPath);
        var json = JsonSerializer.Serialize(recovery, SerializerOptions);
        await File.WriteAllTextAsync(RecoveryPath, json, cancellationToken);
    }

    private async Task<SshTrialRecovery?> ReadRecoveryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RecoveryPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(RecoveryPath, cancellationToken);
            return JsonSerializer.Deserialize<SshTrialRecovery>(json, SerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            logger.LogWarning(exception, "Could not read the SSH trial recovery file");
            return null;
        }
    }

    private void DeleteRecoveryFile()
    {
        if (File.Exists(RecoveryPath))
        {
            File.Delete(RecoveryPath);
        }
    }

    private SshTrialRecovery RequireActiveTrial(Guid trialId)
    {
        if (activeRecovery is null || activeRecovery.Trial.Id != trialId)
        {
            throw new InvalidOperationException("That SSH hardening test is no longer active.");
        }

        return activeRecovery;
    }

    private bool HasFreshVerifiedLogin(string userName, string? fingerprint)
    {
        RemoveExpiredVerifications();
        return verifiedKeyLogins.TryGetValue(NormalizeUserName(userName), out var login) &&
               (string.IsNullOrWhiteSpace(fingerprint) || login.FingerprintSha256 == fingerprint);
    }

    private void RemoveExpiredVerifications()
    {
        var threshold = timeProvider.GetUtcNow().Subtract(VerifiedLoginLifetime);
        foreach (var userName in verifiedKeyLogins.Where(entry => entry.Value.AcceptedAtUtc < threshold).Select(entry => entry.Key).ToArray())
        {
            verifiedKeyLogins.Remove(userName);
        }
    }

    private Task<LinuxCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool requiresSudo,
        string description,
        CancellationToken cancellationToken,
        bool optional = false) =>
        commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, CommandTimeout, description)
            {
                IsOptionalExternalTool = optional
            },
            dryRun: false,
            cancellationToken);

    private static SshHardeningEditor NormalizeEditor(SshHardeningEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Choose an SSH port from 1 to 65535.");
        }

        if (editor.MaxAuthTries is < 1 or > 10)
        {
            throw new InvalidOperationException("Authentication attempts must be from 1 to 10.");
        }

        if (editor.LoginGraceTimeSeconds is < 10 or > 120)
        {
            throw new InvalidOperationException("Login time must be from 10 to 120 seconds.");
        }

        return new SshHardeningEditor
        {
            Port = editor.Port,
            PermitRootLogin = editor.PermitRootLogin,
            PasswordAuthentication = editor.PasswordAuthentication,
            PublicKeyAuthentication = editor.PublicKeyAuthentication,
            KeyboardInteractiveAuthentication = editor.KeyboardInteractiveAuthentication,
            UsePam = editor.UsePam,
            X11Forwarding = editor.X11Forwarding,
            AllowAgentForwarding = editor.AllowAgentForwarding,
            AllowTcpForwarding = editor.AllowTcpForwarding,
            MaxAuthTries = editor.MaxAuthTries,
            LoginGraceTimeSeconds = editor.LoginGraceTimeSeconds
        };
    }

    private static string NormalizeUserName(string userName)
    {
        var normalized = (userName ?? string.Empty).Trim().ToLowerInvariant();
        if (!UserNamePattern.IsMatch(normalized))
        {
            throw new InvalidOperationException("Choose a valid local Linux user.");
        }

        return normalized;
    }

    private static string NormalizePublicKey(string publicKey)
    {
        if (publicKey.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("That is a private key. Keep it on the client device and paste only the single-line .pub key here.");
        }

        var lines = SplitLines(publicKey)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => Regex.Replace(line.Trim(), "\\s+", " "))
            .ToArray();
        if (lines.Length != 1 ||
            !(lines[0].StartsWith("ssh-ed25519 ", StringComparison.Ordinal) ||
              lines[0].StartsWith("sk-ssh-ed25519@openssh.com ", StringComparison.Ordinal) ||
              lines[0].StartsWith("ecdsa-sha2-", StringComparison.Ordinal) ||
              lines[0].StartsWith("ssh-rsa ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Paste one OpenSSH public-key line, normally beginning with ssh-ed25519.");
        }

        return lines[0];
    }

    private static bool IsPublicKeyType(string value) =>
        value.StartsWith("ssh-", StringComparison.Ordinal) ||
        value.StartsWith("ecdsa-", StringComparison.Ordinal) ||
        value.StartsWith("sk-ssh-", StringComparison.Ordinal);

    private static string ParseFingerprint(string output)
    {
        var fingerprint = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith("SHA256:", StringComparison.Ordinal));
        return fingerprint is null ? throw new InvalidOperationException("ssh-keygen did not return a SHA256 fingerprint for that key.") : NormalizeFingerprint(fingerprint);
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        var normalized = (fingerprint ?? string.Empty).Trim().TrimEnd('=');
        if (!FingerprintPattern.IsMatch(normalized))
        {
            throw new InvalidOperationException("The SSH public-key fingerprint is not valid.");
        }

        return normalized;
    }

    private static int ParseDurationSeconds(string value, int fallback)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        if (value.EndsWith('m') && int.TryParse(value[..^1], out var minutes))
        {
            return minutes * 60;
        }

        return fallback;
    }

    private static bool IsRootLoginAllowed(string value) =>
        value is "yes" or "prohibit-password" or "without-password" or "forced-commands-only";

    private static bool IsInteractiveShell(string shell) =>
        !string.IsNullOrWhiteSpace(shell) &&
        !shell.EndsWith("/nologin", StringComparison.Ordinal) &&
        !shell.EndsWith("/false", StringComparison.Ordinal);

    private static void EnsureSafeHomePath(string path)
    {
        if (!IsSafeHomePath(path))
        {
            throw new InvalidOperationException("The selected Linux account has an unsafe or missing home directory.");
        }
    }

    private static bool IsSafeHomePath(string path) =>
        Path.IsPathRooted(path) && path.Length > 1 && !path.Contains('\0');

    private static bool IsPathInsideHome(string path, string homeDirectory)
    {
        var normalizedHome = Path.GetFullPath(homeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedHome, StringComparison.Ordinal);
    }

    private static void EnsureSafeAuthorizedKeysPath(string path, string homeDirectory)
    {
        if (IsPathInsideHome(path, homeDirectory))
        {
            return;
        }

        const string systemSshDirectory = "/etc/ssh/";
        if (Path.GetFullPath(path).StartsWith(systemSshDirectory, StringComparison.Ordinal) &&
            Path.GetFullPath(path).Length > systemSshDirectory.Length)
        {
            return;
        }

        throw new InvalidOperationException(
            "OpenSSH points this user's authorized_keys outside their home and /etc/ssh. LMS will not write to that custom path automatically.");
    }

    private static string ResolveServiceName() =>
        File.Exists("/lib/systemd/system/sshd.service") || File.Exists("/etc/systemd/system/sshd.service") ? "sshd" : "ssh";

    private static void EnsureSuccess(LinuxCommandResult result, string prefix)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{prefix}: {BuildFailureDetail(result)}");
        }
    }

    private static string BuildFailureDetail(LinuxCommandResult result) =>
        FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}";

    private static string? FirstNonEmptyLine(params string[] values) =>
        values.SelectMany(SplitLines).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string QuoteShellArgument(string value) =>
        string.IsNullOrEmpty(value) ? "''" : $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lms-ssh-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string RemoveMarkedBlock(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return value;
        }

        var end = value.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException("The existing LMS SSH authenticator PAM block is incomplete. Repair it manually before continuing.");
        }

        end += endMarker.Length;
        while (end < value.Length && value[end] is '\r' or '\n')
        {
            end++;
        }

        return value.Remove(start, end - start);
    }

    private static IReadOnlyList<string> GenerateRecoveryCodes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        while (codes.Count < 5)
        {
            codes.Add(RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture));
        }

        return codes.ToArray();
    }

    private static void MakeTemporaryFileReadableByTargetUser(string directoryPath, string filePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("SSH administration is available only on Linux hosts.");
        }

        File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                           UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        trialCancellation?.Cancel();
        trialCancellation?.Dispose();
        operationGate.Dispose();
    }

    private sealed record VerifiedKeyLogin(string FingerprintSha256, DateTimeOffset AcceptedAtUtc);
    private sealed record UserSshContext(
        string AuthorizedKeysPath,
        bool PublicKeyAuthentication,
        bool PasswordAuthentication,
        string AuthenticationMethods);
    internal sealed record KeyOnboardingEntry(string UserName, string AuthorizedKeysPath);
    private sealed record SshTrialRecovery(SshAdminTrialViewModel Trial, bool HadManagedConfiguration, string ManagedConfiguration);
    private sealed record PackageCommand(string FileName, IReadOnlyList<string> Arguments, string Description);
}
