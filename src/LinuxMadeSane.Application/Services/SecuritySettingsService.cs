using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public sealed class SecuritySettingsService(
    ISecurityUserStore securityUserStore,
    ITrustedNetworkStore trustedNetworkStore,
    ISecretStore secretStore,
    IRemoteAccessSystemService remoteAccessSystemService) : ISecuritySettingsService
{
    public async Task<SecuritySettingsPageViewModel> GetPageAsync(CancellationToken cancellationToken = default)
    {
        var trustedNetworks = await trustedNetworkStore.ListAsync(cancellationToken);
        var users = await securityUserStore.ListAsync(cancellationToken);

        return new SecuritySettingsPageViewModel(
            trustedNetworks
                .OrderByDescending(entry => entry.IsEnabled)
                .ThenByDescending(entry => entry.IsBuiltIn)
                .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .Select(MapTrustedNetwork)
                .ToArray(),
            users
                .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                .Select(MapUser)
                .ToArray());
    }

    public async Task<SecurityUserProvisioningViewModel> CreateUserAsync(
        SecurityUserEditor editor,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(editor.Email);
        var existing = await securityUserStore.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("An LMS account with that email already exists.");
        }

        ValidateAuthorizedKeys(editor.SshAuthenticationMode, editor.AuthorizedKeyEntries);

        var secret = TotpAuthenticator.GenerateSecret();
        var secretReference = await secretStore.StoreSecretAsync(secret, $"security-user-otp:{normalizedEmail}", cancellationToken);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var (linuxUsername, isLocalAccountManaged) = await ResolveLinuxUsernameForCreateAsync(
                normalizedEmail,
                editor.LinuxUsername,
                cancellationToken);

            var user = new SecurityUser(
                Guid.NewGuid(),
                normalizedEmail,
                linuxUsername,
                true,
                editor.SshAuthenticationMode,
                NormalizeAuthorizedKeyEntries(editor.AuthorizedKeyEntries),
                isLocalAccountManaged,
                secretReference,
                now,
                now,
                null,
                null);

            await securityUserStore.SaveAsync(user, cancellationToken);
            await ApplyRemoteAccessConfigurationAsync(cancellationToken);
            return BuildProvisioningResult(user, secret);
        }
        catch
        {
            await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
            throw;
        }
    }

    public async Task<SecurityUserAccessEditor> GetUserEditorAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        return new SecurityUserAccessEditor
        {
            Id = user.Id,
            Email = user.Email,
            LinuxUsername = ResolveLinuxUsername(user),
            IsEnabled = user.IsEnabled,
            SshAuthenticationMode = user.SshAuthenticationMode,
            AuthorizedKeyEntries = user.AuthorizedKeyEntries
        };
    }

    public async Task SaveUserAsync(SecurityUserAccessEditor editor, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(editor.Id, cancellationToken);
        var currentLinuxUsername = ResolveLinuxUsername(user);
        var linuxUsername = NormalizeLinuxUsername(editor.LinuxUsername);
        ValidateLinuxUsername(linuxUsername);
        ValidateAuthorizedKeys(editor.SshAuthenticationMode, editor.AuthorizedKeyEntries);

        var existingLinuxUsername = await securityUserStore.FindByLinuxUsernameAsync(linuxUsername, cancellationToken);
        if (existingLinuxUsername is not null && existingLinuxUsername.Id != user.Id)
        {
            throw new InvalidOperationException("An LMS account with that Linux username already exists.");
        }

        var linuxUsernameChanged = !string.Equals(currentLinuxUsername, linuxUsername, StringComparison.Ordinal);
        var createdLocalAccount = linuxUsernameChanged
            ? await remoteAccessSystemService.EnsureLocalAccountAsync(linuxUsername, cancellationToken)
            : false;

        if (linuxUsernameChanged && !createdLocalAccount)
        {
            throw new InvalidOperationException("That Linux username already exists on this machine. Choose a new dedicated local login for this LMS account.");
        }

        var updated = user with
        {
            LinuxUsername = linuxUsername,
            IsEnabled = editor.IsEnabled,
            SshAuthenticationMode = editor.SshAuthenticationMode,
            AuthorizedKeyEntries = NormalizeAuthorizedKeyEntries(editor.AuthorizedKeyEntries),
            IsLocalAccountManaged = user.IsLocalAccountManaged || createdLocalAccount,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await securityUserStore.SaveAsync(updated, cancellationToken);
        await ApplyRemoteAccessConfigurationAsync(cancellationToken);
    }

    public async Task<SecurityUserProvisioningViewModel> ResetUserOtpAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(user.OtpSecretReference))
        {
            await secretStore.DeleteSecretAsync(user.OtpSecretReference, cancellationToken);
        }

        var secret = TotpAuthenticator.GenerateSecret();
        var secretReference = await secretStore.StoreSecretAsync(secret, $"security-user-otp:{NormalizeEmail(user.Email)}", cancellationToken);
        var updated = user with
        {
            OtpSecretReference = secretReference,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await securityUserStore.SaveAsync(updated, cancellationToken);
        return BuildProvisioningResult(updated, secret);
    }

    public async Task<SecurityUserPasswordResetViewModel> BuildPasswordResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        return new SecurityUserPasswordResetViewModel(
            user.Id,
            user.Email,
            ResolveLinuxUsername(user),
            GenerateSuggestedPassword());
    }

    public async Task ResetUserPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        var normalizedPassword = newPassword?.Trim() ?? string.Empty;
        if (normalizedPassword.Length < 14)
        {
            throw new InvalidOperationException("Choose a stronger password with at least 14 characters.");
        }

        var linuxUsername = ResolveLinuxUsername(user);
        await remoteAccessSystemService.EnsureLocalAccountAsync(linuxUsername, cancellationToken);
        await remoteAccessSystemService.ResetLocalPasswordAsync(linuxUsername, normalizedPassword, cancellationToken);

        await securityUserStore.SaveAsync(user with
        {
            LinuxUsername = linuxUsername,
            PasswordChangedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task SetUserEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);

        await securityUserStore.SaveAsync(user with
        {
            LinuxUsername = ResolveLinuxUsername(user),
            IsEnabled = isEnabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

        await ApplyRemoteAccessConfigurationAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await securityUserStore.GetAsync(userId, cancellationToken);
        if (user is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(user.OtpSecretReference))
        {
            await secretStore.DeleteSecretAsync(user.OtpSecretReference, cancellationToken);
        }

        if (user.IsLocalAccountManaged)
        {
            await remoteAccessSystemService.DeleteLocalAccountAsync(ResolveLinuxUsername(user), cancellationToken);
        }

        await securityUserStore.DeleteAsync(userId, cancellationToken);
        await ApplyRemoteAccessConfigurationAsync(cancellationToken);
    }

    public async Task<Guid> SaveTrustedNetworkAsync(TrustedNetworkEntryEditor editor, CancellationToken cancellationToken = default)
    {
        var normalizedAddress = editor.AddressOrCidr.Trim();
        if (!TrustedNetworkMatcher.IsValidAddressOrCidr(normalizedAddress))
        {
            throw new InvalidOperationException("Enter a valid IP address or CIDR range.");
        }

        var existing = editor.Id.HasValue
            ? await trustedNetworkStore.GetAsync(editor.Id.Value, cancellationToken)
            : null;

        if (existing is not null && existing.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in trusted network entries cannot be edited.");
        }

        var now = DateTimeOffset.UtcNow;
        var isTrustedAccessEnabled = editor.IsEnabled && !editor.IsAuthenticationEnabled;
        var entry = new TrustedNetworkEntry(
            editor.Id ?? Guid.NewGuid(),
            editor.Label.Trim(),
            normalizedAddress,
            editor.Description.Trim(),
            editor.IsEnabled,
            isTrustedAccessEnabled,
            editor.IsAuthenticationEnabled,
            existing?.IsBuiltIn ?? false,
            existing?.CreatedAtUtc ?? now,
            now);

        await trustedNetworkStore.SaveAsync(entry, cancellationToken);
        return entry.Id;
    }

    public async Task SetTrustedNetworkEnabledAsync(Guid entryId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var entry = await trustedNetworkStore.GetAsync(entryId, cancellationToken)
            ?? throw new InvalidOperationException("Trusted network entry was not found.");

        await trustedNetworkStore.SaveAsync(entry with
        {
            IsEnabled = isEnabled,
            IsTrustedAccessEnabled = isEnabled && !entry.IsAuthenticationEnabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task SetTrustedNetworkTrustedAccessEnabledAsync(Guid entryId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var entry = await trustedNetworkStore.GetAsync(entryId, cancellationToken)
            ?? throw new InvalidOperationException("Trusted network entry was not found.");

        await trustedNetworkStore.SaveAsync(entry with
        {
            IsTrustedAccessEnabled = isEnabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task SetTrustedNetworkAuthenticationEnabledAsync(Guid entryId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var entry = await trustedNetworkStore.GetAsync(entryId, cancellationToken)
            ?? throw new InvalidOperationException("Trusted network entry was not found.");

        await trustedNetworkStore.SaveAsync(entry with
        {
            IsAuthenticationEnabled = isEnabled,
            IsTrustedAccessEnabled = entry.IsEnabled && !isEnabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task DeleteTrustedNetworkAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = await trustedNetworkStore.GetAsync(entryId, cancellationToken);
        if (entry is null)
        {
            return;
        }

        if (entry.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in trusted network entries cannot be deleted.");
        }

        await trustedNetworkStore.DeleteAsync(entryId, cancellationToken);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string NormalizeLinuxUsername(string value) => value.Trim().ToLowerInvariant();

    private static string ResolveLinuxUsername(SecurityUser user) =>
        string.IsNullOrWhiteSpace(user.LinuxUsername)
            ? DeriveLinuxUsername(user.Email)
            : NormalizeLinuxUsername(user.LinuxUsername);

    private static string DeriveLinuxUsername(string email)
    {
        var rawCandidate = email.Split('@', 2)[0].Trim().ToLowerInvariant();
        var sanitized = new string(rawCandidate
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray())
            .Trim('-');

        var candidate = string.IsNullOrWhiteSpace(sanitized) ? "lms-user" : sanitized;
        return char.IsLetter(candidate[0]) || candidate[0] == '_'
            ? candidate
            : $"u-{candidate}";
    }

    private async Task<(string LinuxUsername, bool IsLocalAccountManaged)> ResolveLinuxUsernameForCreateAsync(
        string normalizedEmail,
        string requestedLinuxUsername,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedLinuxUsername))
        {
            var linuxUsername = NormalizeLinuxUsername(requestedLinuxUsername);
            ValidateLinuxUsername(linuxUsername);

            var existingLinuxUsername = await securityUserStore.FindByLinuxUsernameAsync(linuxUsername, cancellationToken);
            if (existingLinuxUsername is not null)
            {
                throw new InvalidOperationException("An LMS account with that Linux username already exists.");
            }

            var isLocalAccountManaged = await remoteAccessSystemService.EnsureLocalAccountAsync(linuxUsername, cancellationToken);
            if (!isLocalAccountManaged)
            {
                throw new InvalidOperationException("That Linux username already exists on this machine. Choose a new dedicated local login for this LMS account.");
            }

            return (linuxUsername, true);
        }

        var baseLinuxUsername = DeriveLinuxUsername(normalizedEmail);
        for (var index = 0; index < 1000; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index + 1}";
            var maxBaseLength = 32 - suffix.Length;
            var trimmedBase = baseLinuxUsername.Length > maxBaseLength
                ? baseLinuxUsername[..maxBaseLength].TrimEnd('-', '_')
                : baseLinuxUsername;

            if (string.IsNullOrWhiteSpace(trimmedBase))
            {
                trimmedBase = "lms-user";
            }

            var candidate = $"{trimmedBase}{suffix}";
            ValidateLinuxUsername(candidate);

            var existingLinuxUsername = await securityUserStore.FindByLinuxUsernameAsync(candidate, cancellationToken);
            if (existingLinuxUsername is not null)
            {
                continue;
            }

            var isLocalAccountManaged = await remoteAccessSystemService.EnsureLocalAccountAsync(candidate, cancellationToken);
            if (isLocalAccountManaged)
            {
                return (candidate, true);
            }
        }

        throw new InvalidOperationException("Linux Made Sane could not find a free local login name for that LMS account.");
    }

    private static void ValidateLinuxUsername(string linuxUsername)
    {
        if (string.IsNullOrWhiteSpace(linuxUsername))
        {
            throw new InvalidOperationException("Enter a Linux username for SSH access.");
        }

        if (linuxUsername.Length > 32)
        {
            throw new InvalidOperationException("Linux usernames must be 32 characters or fewer.");
        }

        if (!(char.IsLetter(linuxUsername[0]) || linuxUsername[0] == '_') ||
            linuxUsername.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidOperationException("Linux usernames must start with a letter or underscore and only contain letters, numbers, hyphens, or underscores.");
        }
    }

    private static void ValidateAuthorizedKeys(RemoteAccessSshAuthenticationMode mode, string authorizedKeyEntries)
    {
        if (mode is not RemoteAccessSshAuthenticationMode.Password &&
            string.IsNullOrWhiteSpace(authorizedKeyEntries))
        {
            throw new InvalidOperationException("Key-based SSH modes require at least one imported OpenSSH public key or certificate-authority entry.");
        }
    }

    private static string NormalizeAuthorizedKeyEntries(string value) =>
        string.Join(
            Environment.NewLine,
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static SecurityUserProvisioningViewModel BuildProvisioningResult(SecurityUser user, string secret)
    {
        var manualEntryKey = TotpAuthenticator.FormatManualEntryKey(secret);
        return new SecurityUserProvisioningViewModel(
            user.Id,
            user.Email,
            ResolveLinuxUsername(user),
            user.SshAuthenticationMode,
            manualEntryKey,
            TotpAuthenticator.BuildOtpUri(user.Email, secret));
    }

    private static SecurityUserViewModel MapUser(SecurityUser user) =>
        new(
            user.Id,
            user.Email,
            ResolveLinuxUsername(user),
            user.IsEnabled,
            user.SshAuthenticationMode,
            !string.IsNullOrWhiteSpace(user.AuthorizedKeyEntries),
            user.IsLocalAccountManaged,
            !string.IsNullOrWhiteSpace(user.OtpSecretReference),
            user.LastLoginAtUtc,
            user.PasswordChangedAtUtc);

    private static TrustedNetworkEntryViewModel MapTrustedNetwork(TrustedNetworkEntry entry) =>
        new(
            entry.Id,
            entry.Label,
            entry.AddressOrCidr,
            entry.Description,
            entry.IsEnabled,
            entry.IsEnabled && !entry.IsAuthenticationEnabled,
            entry.IsAuthenticationEnabled,
            entry.IsBuiltIn);

    private async Task<SecurityUser> GetRequiredUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await securityUserStore.GetAsync(userId, cancellationToken)
        ?? throw new InvalidOperationException("LMS account was not found.");

    private async Task ApplyRemoteAccessConfigurationAsync(CancellationToken cancellationToken)
    {
        var users = await securityUserStore.ListAsync(cancellationToken);
        var normalizedUsers = users
            .Select(user => user with
            {
                LinuxUsername = ResolveLinuxUsername(user),
                AuthorizedKeyEntries = NormalizeAuthorizedKeyEntries(user.AuthorizedKeyEntries)
            })
            .ToArray();

        await remoteAccessSystemService.ApplySshAccessConfigurationAsync(normalizedUsers, cancellationToken);
    }

    private static string GenerateSuggestedPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^*-_=+?";
        var all = upper + lower + digits + symbols;
        Span<char> password = stackalloc char[20];

        password[0] = upper[Random.Shared.Next(upper.Length)];
        password[1] = lower[Random.Shared.Next(lower.Length)];
        password[2] = digits[Random.Shared.Next(digits.Length)];
        password[3] = symbols[Random.Shared.Next(symbols.Length)];

        for (var index = 4; index < password.Length; index++)
        {
            password[index] = all[Random.Shared.Next(all.Length)];
        }

        for (var index = password.Length - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (password[index], password[swapIndex]) = (password[swapIndex], password[index]);
        }

        return new string(password);
    }
}
