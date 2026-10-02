// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Shares;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class UserCredentialTrialService(IServiceScopeFactory scopes, SshAdminStorageSettings storage,
    TimeProvider clock, ILogger<UserCredentialTrialService> logger) : BackgroundService, IUserCredentialTrialService
{
    internal static SemaphoreSlim ConfigurationGate { get; } = new(1, 1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Recovery? active;
    private bool startupRecoveryPending;
    private string RecoveryPath => Path.Combine(storage.DirectoryPath, "pending-user-credentials-trial.json");
    internal static readonly TimeSpan TrialDuration = TimeSpan.FromMinutes(2);

    public override async Task StartAsync(CancellationToken token)
    {
        if (File.Exists(RecoveryPath))
        {
            try
            {
                startupRecoveryPending = true;
                active = JsonSerializer.Deserialize<Recovery>(await File.ReadAllTextAsync(RecoveryPath, token))
                    ?? throw new InvalidOperationException("The pending user credential recovery record is invalid.");
                if (active.SnapshotReady) await RestoreAsync(active, token);
                else
                {
                    using var scope = scopes.CreateScope();
                    await ControlAsync(scope, "discard", active, null, token);
                    File.Delete(RecoveryPath);
                    active = null;
                    startupRecoveryPending = false;
                }
            }
            catch (Exception ex) { logger.LogError(ex, "User credential recovery remains pending; new trials are blocked"); }
        }
        await base.StartAsync(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
                await gate.WaitAsync(stoppingToken);
                try { if (active is not null && (startupRecoveryPending || clock.GetUtcNow() >= active.Trial.ExpiresAtUtc)) await RestoreAsync(active, stoppingToken); }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Could not restore the pending user login trial; recovery will retry"); }
        }
    }

    public override async Task StopAsync(CancellationToken token)
    {
        await base.StopAsync(token);
        await gate.WaitAsync(CancellationToken.None);
        try { if (active is not null) await RestoreAsync(active, CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "User login watchdog remains armed after LMS shutdown"); }
        finally { gate.Release(); }
    }

    public async Task<UserCredentialTrial?> GetActiveTrialAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (active is not null && (startupRecoveryPending || clock.GetUtcNow() >= active.Trial.ExpiresAtUtc)) await RestoreAsync(active, token);
            return active?.Trial;
        }
        finally { gate.Release(); }
    }

    public async Task<UserCredentialTrial> StartTrialAsync(Guid userId, LocalUserAccessEditor? editor, string? newPassword = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        var configurationGateHeld = false;
        try
        {
            await ConfigurationGate.WaitAsync(cancellationToken);
            configurationGateHeld = true;
            if (active is not null) throw new InvalidOperationException($"Finish or revert the active login test for {active.Trial.UserName} first.");
            if (File.Exists(RecoveryPath))
                throw new InvalidOperationException("Previous user credential recovery is still pending. Resolve it before starting another login test.");
            if (File.Exists(Path.Combine(storage.DirectoryPath, "pending-ssh-hardening-trial.json")))
                throw new InvalidOperationException("Finish the SSH server settings trial before testing user credentials.");
            using var scope = scopes.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<ILinuxShareModuleDataService>();
            var user = await data.GetUserAsync(userId, cancellationToken) ?? throw new InvalidOperationException("The Linux user no longer exists.");
            if (editor is not null && editor.UserName != user.UserName) throw new InvalidOperationException("The credentials must belong to the selected Linux user.");
            if (editor is null && string.IsNullOrWhiteSpace(newPassword)) throw new InvalidOperationException("No credential change was supplied.");
            if (newPassword is not null && (newPassword.Length < 14 || newPassword.Contains('\n') || newPassword.Contains('\r')))
                throw new InvalidOperationException("Choose a password with at least 14 characters and no line breaks.");
            if (editor is not null)
            {
                if (!Enum.IsDefined(editor.SshAuthenticationMode)) throw new InvalidOperationException("Select a supported SSH login mode.");
                var keys = editor.AuthorizedKeyEntries.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith('#')).ToArray();
                if (editor.SshAuthenticationMode is RemoteAccessSshAuthenticationMode.KeyOnly or RemoteAccessSshAuthenticationMode.PasswordAndKey && keys.Length == 0)
                    throw new InvalidOperationException("Import a public key before testing a key-based login mode.");
                if (keys.Any(line => !LocalSshAdminService.TryParseAuthorizedKey(line, out _)))
                    throw new InvalidOperationException("Import valid OpenSSH public key lines. Never paste a private key.");
                foreach (var key in keys)
                    await RunAsync(scope, new LinuxCommandRequest("ssh-keygen", ["-l", "-E", "sha256", "-f", "/dev/stdin"], false,
                        TimeSpan.FromSeconds(10), "Validate the imported public key") { StandardInputBytes = Encoding.UTF8.GetBytes(key + "\n") }, cancellationToken);
            }
            var old = await data.GetUserAccessPolicyAsync(user.UserName, cancellationToken);
            var desired = editor is null ? old : new LocalUserAccessPolicy(user.UserName, true, editor.SshAuthenticationMode,
                editor.AuthorizedKeyEntries.Trim(), clock.GetUtcNow(), old?.PasswordChangedAtUtc);
            if (newPassword is not null)
            {
                var effective = await ReadEffectiveAsync(scope, user.UserName, cancellationToken);
                if (effective.GetValueOrDefault("passwordauthentication") != "yes")
                    throw new InvalidOperationException("This user does not accept SSH passwords. Test a password-capable SSH login mode first. The password has not changed.");
                desired = (desired ?? new LocalUserAccessPolicy(user.UserName, false, RemoteAccessSshAuthenticationMode.Password, "", clock.GetUtcNow(), null))
                    with { PasswordChangedAtUtc = clock.GetUtcNow() };
            }
            var now = clock.GetUtcNow();
            var trial = new UserCredentialTrial(Guid.NewGuid(), userId, user.UserName, now, now.Add(TrialDuration), newPassword is not null) { ProposedMode = editor?.SshAuthenticationMode, ProposedPublicKeys = editor?.AuthorizedKeyEntries };
            var recovery = new Recovery(trial, old, desired, editor is not null, false);
            Directory.CreateDirectory(storage.DirectoryPath);
            await using (var stream = new FileStream(RecoveryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, recovery, cancellationToken: cancellationToken);
            active = recovery;
            var liveChangesStarted = false;
            try
            {
                await ControlAsync(scope, "snapshot", recovery, new { changesPassword = trial.ChangesPassword,
                    seconds = (int)TrialDuration.TotalSeconds, expires = trial.ExpiresAtUtc.ToUnixTimeMilliseconds() / 1000d, rollbackScript = UserCredentialRecoveryScripts.Rollback }, cancellationToken);
                recovery = recovery with { SnapshotReady = true };
                await PersistAsync(recovery, cancellationToken);
                active = recovery;
                liveChangesStarted = true;
                Dictionary<string, string>? effectivePolicy = null;
                if (editor is not null)
                {
                    await ControlAsync(scope, "apply", recovery, new {
                        configuration = LocalUserAccessSystemService.BuildSshdConfig([desired!]) + "\nMatch all\n",
                        publicKeys = desired!.AuthorizedKeyEntries + "\n" }, cancellationToken);
                    effectivePolicy = await ReadEffectiveAsync(scope, user.UserName, cancellationToken);
                    RequireEffectiveMode(effectivePolicy, desired!);
                }
                if (newPassword is not null)
                    await ControlAsync(scope, "password", recovery, new { password = newPassword }, cancellationToken);
                // Ignore journal entries recorded before all proposed credentials were installed.
                effectivePolicy ??= await ReadEffectiveAsync(scope, user.UserName, cancellationToken);
                recovery = recovery with { Trial = trial with { StartedAtUtc = clock.GetUtcNow(),
                    SshPort = int.TryParse(effectivePolicy.GetValueOrDefault("port"), out var port) ? port : null } };
                await PersistAsync(recovery, cancellationToken);
                active = recovery;
                return recovery.Trial;
            }
            catch
            {
                if (liveChangesStarted) await RestoreAsync(recovery, CancellationToken.None);
                else
                {
                    await ControlAsync(scope, "discard", recovery, null, CancellationToken.None);
                    File.Delete(RecoveryPath);
                    active = null;
                    startupRecoveryPending = false;
                }
                throw;
            }
        }
        finally { if (configurationGateHeld) ConfigurationGate.Release(); gate.Release(); }
    }

    public async Task ConfirmAsync(Guid trialId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var recovery = RequireTrial(trialId);
            if (clock.GetUtcNow() >= recovery.Trial.ExpiresAtUtc)
            {
                await RestoreAsync(recovery, token);
                throw new InvalidOperationException("The login test expired. Previous credentials and SSH settings were restored.");
            }
            using var scope = scopes.CreateScope();
            var result = await RunAsync(scope, new LinuxCommandRequest("journalctl",
                ["--since", $"@{recovery.Trial.StartedAtUtc.ToUnixTimeSeconds()}", "-u", "ssh.service", "-u", "sshd.service", "--no-pager", "-o", "json"], true,
                TimeSpan.FromSeconds(15), "Verify a fresh login for the user credential trial"), token);
            var sourceAddress = FindMatchingLoginAddress(result.StandardOutput, recovery);
            if (sourceAddress is null)
                throw new InvalidOperationException("No fresh successful login with the proposed method is visible yet. Open a new SSH connection from your device, then check again, or revert.");
            if (recovery.ChangesPolicy)
                RequireEffectiveMode(await ReadEffectiveAsync(scope, recovery.Trial.UserName, token, sourceAddress), recovery.Desired!);
            try
            {
                await ControlAsync(scope, "keep", recovery, null, token);
                await SavePolicyAsync(scope, recovery.Trial.UserName, recovery.Desired, token);
                File.Delete(RecoveryPath);
                active = null;
                startupRecoveryPending = false;
            }
            catch
            {
                await RestoreAsync(recovery, CancellationToken.None);
                throw;
            }
            await DiscardAsync(scope, recovery);
        }
        finally { gate.Release(); }
    }

    public async Task RevertAsync(Guid trialId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { await RestoreAsync(RequireTrial(trialId), token); }
        finally { gate.Release(); }
    }

    private Recovery RequireTrial(Guid id) => active?.Trial.Id == id ? active : throw new InvalidOperationException("This login test is no longer active. Its previous settings may already have been restored.");

    private async Task RestoreAsync(Recovery recovery, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        await ControlAsync(scope, "restore", recovery, null, token);
        await SavePolicyAsync(scope, recovery.Trial.UserName, recovery.Original, token);
        File.Delete(RecoveryPath);
        active = null;
        startupRecoveryPending = false;
        await DiscardAsync(scope, recovery);
    }

    private async Task DiscardAsync(IServiceScope scope, Recovery recovery)
    {
        try { await ControlAsync(scope, "discard", recovery, null, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Protected credential backup cleanup failed for trial {TrialId}", recovery.Trial.Id); }
    }

    private static async Task SavePolicyAsync(IServiceScope scope, string user, LocalUserAccessPolicy? policy, CancellationToken token)
    {
        var db = scope.ServiceProvider.GetRequiredService<LinuxMadeSaneDbContext>();
        var entity = await db.LocalUserAccessPolicies.SingleOrDefaultAsync(p => p.UserName == user, token);
        if (policy is null) { if (entity is not null) db.Remove(entity); }
        else
        {
            if (entity is null) { entity = new LocalUserAccessPolicyEntity { UserName = user }; db.Add(entity); }
            entity.IsManagedPolicy = policy.IsManagedPolicy;
            entity.SshAuthenticationMode = (int)policy.SshAuthenticationMode;
            entity.AuthorizedKeyEntries = policy.AuthorizedKeyEntries;
            entity.UpdatedAtUtc = policy.UpdatedAtUtc;
            entity.PasswordChangedAtUtc = policy.PasswordChangedAtUtc;
        }
        await db.SaveChangesAsync(token);
    }

    private async Task PersistAsync(Recovery recovery, CancellationToken token)
    {
        await File.WriteAllTextAsync(RecoveryPath + ".tmp", JsonSerializer.Serialize(recovery), token);
        File.Move(RecoveryPath + ".tmp", RecoveryPath, true);
    }

    private static async Task ControlAsync(IServiceScope scope, string operation, Recovery recovery, object? payload, CancellationToken token) =>
        _ = await RunAsync(scope, new LinuxCommandRequest("python3",
            ["-c", UserCredentialRecoveryScripts.Control, operation, recovery.Trial.Id.ToString("N"), recovery.Trial.UserName], true,
            TimeSpan.FromSeconds(30), $"{operation} protected recovery for user login trial")
            { StandardInputBytes = payload is null ? null : JsonSerializer.SerializeToUtf8Bytes(payload) }, token);

    private static async Task<LinuxCommandResult> RunAsync(IServiceScope scope, LinuxCommandRequest request, CancellationToken token)
    {
        var result = await scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>().RunAsync(request, false, token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{request.Description} failed: {result.StandardError.Trim()}");
        return result;
    }

    private static async Task<Dictionary<string,string>> ReadEffectiveAsync(IServiceScope scope, string user, CancellationToken token, string sourceAddress = "127.0.0.1")
    {
        var result = await RunAsync(scope, new LinuxCommandRequest("/usr/sbin/sshd", ["-T", "-C", $"user={user},host={sourceAddress},addr={sourceAddress}"], true,
            TimeSpan.FromSeconds(15), "Check effective SSH login policy"), token);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split(' ', 2))
            .Where(parts => parts.Length == 2).GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.First()[1]);
    }

    internal static void RequireEffectiveMode(IReadOnlyDictionary<string,string> effective, LocalUserAccessPolicy policy)
    {
        var mode = policy.SshAuthenticationMode;
        var expected = mode switch
        {
            RemoteAccessSshAuthenticationMode.PasswordOrKey => "publickey password keyboard-interactive",
            RemoteAccessSshAuthenticationMode.PasswordAndKey => "publickey,password publickey,keyboard-interactive",
            RemoteAccessSshAuthenticationMode.KeyOnly => "publickey",
            _ => "password keyboard-interactive"
        };
        if (effective.GetValueOrDefault("passwordauthentication") != (mode == RemoteAccessSshAuthenticationMode.KeyOnly ? "no" : "yes") ||
            effective.GetValueOrDefault("pubkeyauthentication") != (mode == RemoteAccessSshAuthenticationMode.Password ? "no" : "yes") ||
            (expected is not null && effective.GetValueOrDefault("authenticationmethods") != expected))
            throw new InvalidOperationException("Another SSH server or per-user rule overrides this login mode. The trial cannot safely use it; restore the previous settings and review the SSH server rules.");
        if (mode != RemoteAccessSshAuthenticationMode.Password && effective.GetValueOrDefault("authorizedkeysfile") != "/etc/ssh/linuxmadesane/local-users/authorized_keys/%u")
            throw new InvalidOperationException("SSH is using another authorized-keys location. The proposed keys cannot be safely tested with this rule.");
    }

    internal static bool HasMatchingLogin(string journal, Recovery recovery) => FindMatchingLoginAddress(journal, recovery) is not null;

    internal static string? FindMatchingLoginAddress(string journal, Recovery recovery)
    {
        var user = Regex.Escape(recovery.Trial.UserName);
        var method = recovery.Trial.ChangesPassword || recovery.Desired?.SshAuthenticationMode is RemoteAccessSshAuthenticationMode.Password or RemoteAccessSshAuthenticationMode.PasswordAndKey
            ? "(?:password|keyboard-interactive(?:/pam)?)"
            : recovery.Desired?.SshAuthenticationMode == RemoteAccessSshAuthenticationMode.KeyOnly ? "publickey" : "(?:publickey|password|keyboard-interactive(?:/pam)?)";
        foreach (var line in journal.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var entry = JsonDocument.Parse(line);
                if (!entry.RootElement.TryGetProperty("__REALTIME_TIMESTAMP", out var timestamp) ||
                    !long.TryParse(timestamp.GetString(), out var microseconds) ||
                    microseconds <= (recovery.Trial.StartedAtUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10 ||
                    microseconds >= (recovery.Trial.ExpiresAtUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10) continue;
                var message = entry.RootElement.GetProperty("MESSAGE").GetString() ?? string.Empty;
                var match = Regex.Match(message, $@"^Accepted {method} for {user} from (?<address>\S+) port \d+", RegexOptions.CultureInvariant);
                if (match.Success && System.Net.IPAddress.TryParse(match.Groups["address"].Value, out _))
                    return match.Groups["address"].Value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        return null;
    }

    internal sealed record Recovery(UserCredentialTrial Trial, LocalUserAccessPolicy? Original, LocalUserAccessPolicy? Desired, bool ChangesPolicy, bool SnapshotReady = false);
}
