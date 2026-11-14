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
            var onboarding = new SshKeyOnboardingService(ssh, users, trials);
            trial = await onboarding.BeginAsync(userName, key.PublicKey, cancellationToken);

            await ReportAsync(progress, "Testing a fresh connection with the same key...");
            var result = await verify(cancellationToken);
            if (result.Status != ConnectionTestStatus.Succeeded) return result;
            await onboarding.ConfirmAsync(trial.Id, key.PublicKey, cancellationToken);
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
