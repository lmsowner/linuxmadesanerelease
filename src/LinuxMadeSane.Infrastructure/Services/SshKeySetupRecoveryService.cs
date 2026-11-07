// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

// Runs only after setup fails. The existing credential trial owns mutation,
// validation, the independent watchdog, rollback and policy persistence.
public sealed class SshKeySetupRecoveryService(IManagedHostService hosts, ISshAdminService ssh,
    ILinuxShareModuleDataService users, IUserCredentialTrialService trials,
    ILogger<SshKeySetupRecoveryService> logger, ICommandExecutionService commands) : ISshKeySetupRecoveryService
{
    public async Task<HostConnectionTestResult> RecoverAsync(Guid hostId, GeneratedSshKeyPair key,
        Func<CancellationToken, Task<HostConnectionTestResult>> verify,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        var host = (await hosts.ListHostsAsync(cancellationToken)).Single(h => h.Id == hostId);
        if (!AiLocalMachine.IsLocalMachine(hostId))
        {
            await ReportAsync(progress, "Checking whether this SSH entry points to the machine running LMS...");
            try
            {
                // Use the saved SSH transport and the actual machine identity, never
                // a display name, DNS spelling, IP address or account name.
                const string readIdentity = "cat /etc/machine-id";
                var local = AiLocalMachine.CreateManagedHost() with { DefaultWorkingDirectory = "/" };
                var identities = await Task.WhenAll(
                    commands.ExecuteAsync(local, readIdentity, cancellationToken: cancellationToken),
                    commands.ExecuteAsync(host, readIdentity, cancellationToken: cancellationToken));
                var localId = identities[0].StandardOutput.Trim();
                var targetId = identities[1].StandardOutput.Trim();
                if (!identities.All(result => result.IsSuccess) ||
                    !IsMachineId(localId) || !IsMachineId(targetId) ||
                    !string.Equals(localId, targetId, StringComparison.OrdinalIgnoreCase))
                    return Failed("This SSH entry could not be verified as the machine running LMS. Its SSH settings have not been changed. Run key setup on the target LMS machine.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not verify the machine identity for SSH key recovery on host {HostId}", hostId);
                return Failed("LMS could not verify this machine through its saved SSH login. Check that the existing login still connects, then retry key setup.");
            }
        }
        return await RecoverLocalAsync(host.Username, key, verify, progress, cancellationToken);
    }

    private static bool IsMachineId(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit) && value.Any(c => c != '0');

    public async Task<HostConnectionTestResult> RecoverLocalAsync(string userName, GeneratedSshKeyPair key,
        Func<CancellationToken, Task<HostConnectionTestResult>> verify,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        UserCredentialTrial? trial = null;
        try
        {
            await ReportAsync(progress, "Checking the account's SSH settings and installed keys...");
            var overview = await ssh.GetOverviewAsync(cancellationToken);
            if (!overview.IsOpenSshInstalled || !overview.IsServiceActive || !overview.IsConfigurationValid)
                return Failed("Open SSH Server to restore an active SSH service with valid settings, then retry key setup. " + overview.ConfigurationValidationMessage);
            var account = overview.Users.SingleOrDefault(u => u.UserName == userName);
            if (account is null || account.IsLocked)
                return Failed("This account is locked or does not have an interactive SSH login. Enable its intended login access in Users before retrying.");
            // A key-only setup must never silently remove another required factor.
            if (account.IsAuthenticatorRequired || account.AuthenticationMethods.Contains(','))
                return Failed("This account requires more than a key to log in. Use its configured password or authenticator together with the key; its security policy has been preserved.");
            var user = (await users.ListUsersAsync(cancellationToken)).SingleOrDefault(u => u.UserName == userName);
            if (user is null || !user.IsEnabled)
                return Failed("This Linux account is unavailable. Check its access in Users, then retry key setup.");
            var policy = await users.GetUserAccessPolicyAsync(user.UserName, cancellationToken);
            if (policy?.SshAuthenticationMode == RemoteAccessSshAuthenticationMode.PasswordAndKey)
                return Failed("This account requires both a password and key. Its authentication policy has been preserved.");

            var existingKeys = account.AuthorizedKeys.Select(k => k.AuthorizedKeyEntry)
                .Concat((policy?.AuthorizedKeyEntries ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                .Append(key.PublicKey).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal);
            await ReportAsync(progress, "Repairing key access while keeping the existing login available...");
            trial = await trials.StartTrialAsync(user.Id, new LocalUserAccessEditor
            {
                UserName = user.UserName,
                SshAuthenticationMode = account.IsPasswordAuthenticationEnabled
                    ? RemoteAccessSshAuthenticationMode.PasswordOrKey : RemoteAccessSshAuthenticationMode.KeyOnly,
                AuthorizedKeyEntries = string.Join('\n', existingKeys)
            }, cancellationToken: cancellationToken);

            await ReportAsync(progress, "Testing a fresh connection with the same key...");
            var result = await verify(cancellationToken);
            if (result.Status != ConnectionTestStatus.Succeeded) return result;
            var accepted = await ssh.VerifyRecentPublicKeyLoginAsync(user.UserName, key.FingerprintSha256,
                trial.StartedAtUtc, cancellationToken);
            if (!accepted.Succeeded) return Failed(accepted.Message);
            await trials.ConfirmAsync(trial.Id, cancellationToken);
            trial = null;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SSH key setup recovery failed for account {UserName}", userName);
            return Failed(exception.Message);
        }
        finally
        {
            if (trial is not null && (await trials.GetActiveTrialAsync(CancellationToken.None))?.Id == trial.Id)
            {
                await ReportAsync(progress, "Restoring the previous SSH settings...");
                // A rollback failure must propagate; never claim restoration when it failed.
                await trials.RevertAsync(trial.Id, CancellationToken.None);
            }
        }
    }

    private static Task ReportAsync(Func<string, Task>? progress, string message) => progress?.Invoke(message) ?? Task.CompletedTask;

    private static HostConnectionTestResult Failed(string detail) => new(ConnectionTestStatus.Failed,
        "Key setup needs attention. Your saved host credentials have not changed.", detail, DateTimeOffset.UtcNow);
}
