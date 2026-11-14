// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Infrastructure.Services;

// The target LMS owns policy, key placement, verification and the independent rollback timer.
public sealed class SshKeyOnboardingService(ISshAdminService ssh, ILinuxShareModuleDataService users,
    IUserCredentialTrialService trials)
{
    public async Task<UserCredentialTrial> BeginAsync(string userName, string publicKey, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 16384 || publicKey.Contains('\n') || publicKey.Contains('\r') ||
            !LocalSshAdminService.TryParseAuthorizedKey(publicKey, out _))
            throw new InvalidOperationException("Supply one valid OpenSSH public key. The private key must stay on the connecting LMS.");
        var overview = await ssh.GetOverviewAsync(token);
        if (!overview.IsOpenSshInstalled || !overview.IsServiceActive || !overview.IsConfigurationValid)
            throw new InvalidOperationException("The target SSH service must be active with valid settings. " + overview.ConfigurationValidationMessage);
        var account = overview.Users.SingleOrDefault(u => u.UserName == userName);
        if (account is null || account.IsLocked)
            throw new InvalidOperationException("This account is locked or does not have an interactive SSH login.");
        var user = (await users.ListUsersAsync(token)).SingleOrDefault(u => u.UserName == userName);
        if (user is null || !user.IsEnabled)
            throw new InvalidOperationException("This Linux account is unavailable. Its access has not been changed.");
        var policy = await users.GetUserAccessPolicyAsync(userName, token);
        if (account.IsAuthenticatorRequired || account.AuthenticationMethods.Contains(',') ||
            policy?.SshAuthenticationMode == RemoteAccessSshAuthenticationMode.PasswordAndKey)
            throw new InvalidOperationException("This account requires additional authentication. Key-only setup cannot remove its password or MFA requirement.");
        var keys = account.AuthorizedKeys.Select(k => k.AuthorizedKeyEntry)
            .Concat((policy?.AuthorizedKeyEntries ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Append(publicKey.Trim()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal);
        return await trials.StartTrialAsync(user.Id, new LocalUserAccessEditor
        {
            UserName = userName,
            SshAuthenticationMode = account.IsPasswordAuthenticationEnabled
                ? RemoteAccessSshAuthenticationMode.PasswordOrKey : RemoteAccessSshAuthenticationMode.KeyOnly,
            AuthorizedKeyEntries = string.Join('\n', keys)
        }, cancellationToken: token);
    }

    public async Task ConfirmAsync(Guid trialId, string publicKey, CancellationToken token = default)
    {
        var trial = await trials.GetActiveTrialAsync(token);
        if (trial?.Id != trialId || string.IsNullOrWhiteSpace(publicKey) || !LocalSshAdminService.TryParseAuthorizedKey(publicKey, out var key) ||
            !(trial.ProposedPublicKeys ?? "").Split('\n').Contains(publicKey.Trim(), StringComparer.Ordinal))
            throw new InvalidOperationException("This key setup is no longer active. Retry from Hosts.");
        var accepted = await ssh.VerifyRecentPublicKeyLoginAsync(trial.UserName, key.FingerprintSha256, trial.StartedAtUtc, token);
        if (!accepted.Succeeded) throw new InvalidOperationException(accepted.Message);
        await trials.ConfirmAsync(trial.Id, token);
    }

    public async Task RevertAsync(Guid trialId, CancellationToken token = default)
    {
        if ((await trials.GetActiveTrialAsync(token))?.Id == trialId)
            await trials.RevertAsync(trialId, token);
    }
}
