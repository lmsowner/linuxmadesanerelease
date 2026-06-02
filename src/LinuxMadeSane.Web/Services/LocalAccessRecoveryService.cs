// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class LocalAccessRecoveryService(
    IConfiguration configuration,
    ISecurityUserStore securityUserStore,
    ILogger<LocalAccessRecoveryService> logger)
{
    private const string Purpose = "linux-made-sane-local-access-recovery";
    private const int CurrentVersion = 1;
    private const int MaximumAttempts = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<bool> HasActiveChallengeAsync(
        string? challengeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(challengeId))
        {
            return false;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var challenge = await ReadChallengeAsync(cancellationToken);
            if (!ChallengeCanBeUsed(challenge, challengeId))
            {
                if (challenge is not null && IsExpired(challenge))
                {
                    DeleteChallengeFile();
                }

                return false;
            }

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<LocalAccessRecoveryResult> RecoverAsync(
        string? email,
        string? challengeId,
        string? recoveryCode,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var challenge = await ReadChallengeAsync(cancellationToken);
            if (!ChallengeCanBeUsed(challenge, challengeId))
            {
                if (challenge is not null && IsExpired(challenge))
                {
                    DeleteChallengeFile();
                }

                return LocalAccessRecoveryResult.Failure("The recovery link has expired. Re-run the installer over SSH with sudo to generate a new one.");
            }

            var normalizedCode = NormalizeRecoveryCode(recoveryCode);
            if (string.IsNullOrWhiteSpace(normalizedCode) ||
                !RecoveryCodeMatches(challenge!, normalizedCode))
            {
                await RecordFailedAttemptAsync(challenge!, cancellationToken);
                return LocalAccessRecoveryResult.Failure("The recovery code was not accepted.");
            }

            var normalizedEmail = NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                await RecordFailedAttemptAsync(challenge!, cancellationToken);
                return LocalAccessRecoveryResult.Failure("Enter the LMS account email to recover.");
            }

            var user = await securityUserStore.FindByEmailAsync(normalizedEmail, cancellationToken);
            if (user is null)
            {
                await RecordFailedAttemptAsync(challenge!, cancellationToken);
                return LocalAccessRecoveryResult.Failure("The recovery code or LMS email was not accepted.");
            }

            var now = DateTimeOffset.UtcNow;
            var recoveredUser = user with
            {
                IsEnabled = true,
                LastLoginAtUtc = now,
                UpdatedAtUtc = now
            };

            await securityUserStore.SaveAsync(recoveredUser, cancellationToken);
            DeleteChallengeFile();
            logger.LogWarning("Local access recovery was used for LMS account {Email}.", recoveredUser.Email);

            return LocalAccessRecoveryResult.Success(recoveredUser);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task RecordFailedAttemptAsync(
        LocalAccessRecoveryChallenge challenge,
        CancellationToken cancellationToken)
    {
        var updated = challenge with
        {
            Attempts = challenge.Attempts + 1
        };

        if (updated.Attempts >= MaximumAttempts)
        {
            DeleteChallengeFile();
            logger.LogWarning("Local access recovery challenge {ChallengeId} was removed after too many failed attempts.", challenge.ChallengeId);
            return;
        }

        await WriteChallengeAsync(updated, cancellationToken);
    }

    private async Task<LocalAccessRecoveryChallenge?> ReadChallengeAsync(CancellationToken cancellationToken)
    {
        var path = ResolveChallengePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LocalAccessRecoveryChallenge>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read local access recovery challenge from {Path}.", path);
            return null;
        }
    }

    private async Task WriteChallengeAsync(
        LocalAccessRecoveryChallenge challenge,
        CancellationToken cancellationToken)
    {
        var path = ResolveChallengePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, challenge, JsonOptions, cancellationToken);
    }

    private void DeleteChallengeFile()
    {
        var path = ResolveChallengePath();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete local access recovery challenge from {Path}.", path);
        }
    }

    private string ResolveChallengePath()
    {
        var configuredPath = configuration["AccessRecovery:ChallengePath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "data", "access-recovery.json")
            : configuredPath.Trim();
    }

    private static bool ChallengeCanBeUsed(
        LocalAccessRecoveryChallenge? challenge,
        string? challengeId) =>
        challenge is not null &&
        challenge.Version == CurrentVersion &&
        string.Equals(challenge.Purpose, Purpose, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(challenge.Salt) &&
        !string.IsNullOrWhiteSpace(challenge.CodeHash) &&
        string.Equals(challenge.ChallengeId, (challengeId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
        challenge.Attempts < MaximumAttempts &&
        !IsExpired(challenge);

    private static bool IsExpired(LocalAccessRecoveryChallenge challenge) =>
        !DateTimeOffset.TryParse(
            challenge.ExpiresAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var expiresAtUtc) ||
        expiresAtUtc <= DateTimeOffset.UtcNow;

    private static bool RecoveryCodeMatches(
        LocalAccessRecoveryChallenge challenge,
        string normalizedCode)
    {
        var computedHash = ComputeCodeHash(challenge.Salt, normalizedCode);
        var expected = Encoding.ASCII.GetBytes((challenge.CodeHash ?? string.Empty).Trim().ToLowerInvariant());
        var actual = Encoding.ASCII.GetBytes(computedHash);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string ComputeCodeHash(string salt, string normalizedCode)
    {
        var bytes = Encoding.UTF8.GetBytes($"{salt}:{normalizedCode}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeRecoveryCode(string? code)
    {
        var builder = new StringBuilder();
        foreach (var character in code ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string NormalizeEmail(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    private sealed record LocalAccessRecoveryChallenge(
        string Purpose,
        int Version,
        string ChallengeId,
        string Salt,
        string CodeHash,
        string CreatedAtUtc,
        string ExpiresAtUtc,
        int Attempts);
}

public sealed record LocalAccessRecoveryResult(
    bool Succeeded,
    SecurityUser? User,
    string ErrorMessage)
{
    public static LocalAccessRecoveryResult Success(SecurityUser user) =>
        new(true, user, string.Empty);

    public static LocalAccessRecoveryResult Failure(string errorMessage) =>
        new(false, null, errorMessage);
}
