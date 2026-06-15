// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Messaging;
using Microsoft.Extensions.Configuration;
using QRCoder;

namespace LinuxMadeSane.Application.Services;

public sealed class SecuritySettingsService(
    ISecurityUserStore securityUserStore,
    ITrustedNetworkStore trustedNetworkStore,
    ISecretStore secretStore,
    IRemoteAccessSystemService remoteAccessSystemService,
    IMessagingEmailSettingsStore messagingEmailSettingsStore,
    IEmailDeliveryService emailDeliveryService,
    IConfiguration? configuration = null) : ISecuritySettingsService
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
                .ToArray(),
            MapMessaging(await messagingEmailSettingsStore.GetAsync(cancellationToken)));
    }

    public async Task<InitialSetupViewModel> GetInitialSetupAsync(CancellationToken cancellationToken = default)
    {
        var users = await securityUserStore.ListAsync(cancellationToken);
        return BuildInitialSetupViewModel(users);
    }

    public async Task<SecurityUserProvisioningViewModel?> GetInitialSetupProvisioningAsync(
        string? lmsLoginUrl = null,
        CancellationToken cancellationToken = default)
    {
        var users = await securityUserStore.ListAsync(cancellationToken);
        var state = BuildInitialSetupViewModel(users);
        if (!state.CanVerify || !state.PendingUserId.HasValue)
        {
            return null;
        }

        var pendingUser = users.Single(user => user.Id == state.PendingUserId.Value);
        var secret = await secretStore.ResolveSecretAsync(pendingUser.OtpSecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("The pending setup authenticator secret could not be loaded.");
        }

        return await BuildProvisioningResultAsync(
            pendingUser,
            secret,
            lmsLoginUrl,
            LoginSetupEmailKind.Created,
            sendEmail: false,
            cancellationToken);
    }

    public async Task<SecurityUserProvisioningViewModel> StartInitialSetupAsync(
        SecurityUserEditor editor,
        string? lmsLoginUrl = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetInitialSetupAsync(cancellationToken);
        if (!state.CanStart)
        {
            throw new InvalidOperationException(state.CanVerify
                ? "Initial setup has already started. Verify the pending MFA code or reset the setup QR."
                : "Initial setup is already complete.");
        }

        return await CreateUserInternalAsync(
            editor,
            isEnabled: false,
            provisionLocalAccount: false,
            lmsLoginUrl,
            LoginSetupEmailKind.Created,
            cancellationToken);
    }

    public async Task<SecurityUserProvisioningViewModel> ResetInitialSetupOtpAsync(
        string? lmsLoginUrl = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetInitialSetupAsync(cancellationToken);
        if (!state.CanVerify || !state.PendingUserId.HasValue)
        {
            throw new InvalidOperationException(state.IsComplete
                ? "Initial setup is already complete."
                : "Initial setup has not started yet.");
        }

        return await ResetUserOtpAsync(state.PendingUserId.Value, lmsLoginUrl, cancellationToken);
    }

    public async Task<SecurityAuthenticationResult> ConfirmInitialSetupOtpAsync(
        Guid userId,
        string otpCode,
        CancellationToken cancellationToken = default)
    {
        var users = await securityUserStore.ListAsync(cancellationToken);
        var state = BuildInitialSetupViewModel(users);
        if (!state.CanVerify || state.PendingUserId != userId)
        {
            return SecurityAuthenticationResult.Failure(state.IsComplete
                ? "Initial setup is already complete."
                : "Initial setup has not started yet.");
        }

        var user = users.Single(candidate => candidate.Id == userId);
        var secret = await secretStore.ResolveSecretAsync(user.OtpSecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return SecurityAuthenticationResult.Failure("The pending setup authenticator secret could not be loaded.");
        }

        if (!TotpAuthenticator.ValidateCode(secret, otpCode))
        {
            return SecurityAuthenticationResult.Failure("The OTP code was not valid.");
        }

        var linuxUsername = ResolveLinuxUsername(user);
        var isLocalAccountManaged = user.IsLocalAccountManaged ||
                                    await remoteAccessSystemService.EnsureLocalAccountAsync(
                                        linuxUsername,
                                        cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var enabledUser = user with
        {
            LinuxUsername = linuxUsername,
            IsEnabled = true,
            IsLocalAccountManaged = isLocalAccountManaged,
            LastLoginAtUtc = now,
            UpdatedAtUtc = now
        };
        await securityUserStore.SaveAsync(enabledUser, cancellationToken);
        await EnableAuthenticationForBuiltInNetworksAsync(cancellationToken);
        await ApplyRemoteAccessConfigurationAsync(cancellationToken);

        return SecurityAuthenticationResult.Success(
            enabledUser.Id,
            enabledUser.Email,
            enabledUser.SessionLifetimeMinutes);
    }

    public async Task<SecurityUserProvisioningViewModel> CreateUserAsync(
        SecurityUserEditor editor,
        string? lmsLoginUrl = null,
        CancellationToken cancellationToken = default) =>
        await CreateUserInternalAsync(
            editor,
            isEnabled: true,
            provisionLocalAccount: true,
            lmsLoginUrl,
            LoginSetupEmailKind.Created,
            cancellationToken);

    private async Task<SecurityUserProvisioningViewModel> CreateUserInternalAsync(
        SecurityUserEditor editor,
        bool isEnabled,
        bool provisionLocalAccount,
        string? lmsLoginUrl,
        LoginSetupEmailKind emailKind,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(editor.Email);
        var existing = await securityUserStore.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("An LMS account with that email already exists.");
        }

        ValidateAuthorizedKeys(editor.SshAuthenticationMode, editor.AuthorizedKeyEntries);
        var sessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(editor.SessionLifetimeMinutes);

        var secret = TotpAuthenticator.GenerateSecret();
        var secretReference = await secretStore.StoreSecretAsync(secret, $"security-user-otp:{normalizedEmail}", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        SecurityUser user;

        try
        {
            var (linuxUsername, isLocalAccountManaged) = await ResolveLinuxUsernameForCreateAsync(
                normalizedEmail,
                editor.LinuxUsername,
                provisionLocalAccount,
                cancellationToken);

            user = new SecurityUser(
                Guid.NewGuid(),
                normalizedEmail,
                linuxUsername,
                isEnabled,
                sessionLifetimeMinutes,
                editor.SshAuthenticationMode,
                NormalizeAuthorizedKeyEntries(editor.AuthorizedKeyEntries),
                isLocalAccountManaged,
                secretReference,
                now,
                now,
                null,
                null);

            await securityUserStore.SaveAsync(user, cancellationToken);
            if (provisionLocalAccount)
            {
                await ApplyRemoteAccessConfigurationAsync(cancellationToken);
            }
        }
        catch
        {
            await secretStore.DeleteSecretAsync(secretReference, cancellationToken);
            throw;
        }

        return await BuildProvisioningResultAsync(
            user,
            secret,
            lmsLoginUrl,
            emailKind,
            sendEmail: true,
            cancellationToken);
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
            SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes),
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

        var updated = user with
        {
            LinuxUsername = linuxUsername,
            IsEnabled = editor.IsEnabled,
            SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(editor.SessionLifetimeMinutes),
            SshAuthenticationMode = editor.SshAuthenticationMode,
            AuthorizedKeyEntries = NormalizeAuthorizedKeyEntries(editor.AuthorizedKeyEntries),
            IsLocalAccountManaged = linuxUsernameChanged ? createdLocalAccount : user.IsLocalAccountManaged,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await securityUserStore.SaveAsync(updated, cancellationToken);
        await ApplyRemoteAccessConfigurationAsync(cancellationToken);
    }

    public async Task SetUserSessionLifetimeAsync(Guid userId, int sessionLifetimeMinutes, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        await securityUserStore.SaveAsync(user with
        {
            SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(sessionLifetimeMinutes),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<SecurityUserProvisioningViewModel> ResetUserOtpAsync(
        Guid userId,
        string? lmsLoginUrl = null,
        CancellationToken cancellationToken = default)
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
        return await BuildProvisioningResultAsync(
            updated,
            secret,
            lmsLoginUrl,
            LoginSetupEmailKind.Reset,
            sendEmail: true,
            cancellationToken);
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

    public async Task<SecurityMessagingSettingsEditor> GetMessagingEditorAsync(CancellationToken cancellationToken = default)
    {
        var settings = await messagingEmailSettingsStore.GetAsync(cancellationToken);
        return new SecurityMessagingSettingsEditor
        {
            IsEnabled = settings.IsEnabled,
            Provider = settings.Provider,
            SenderAddress = settings.SenderAddress,
            SenderDisplayName = settings.SenderDisplayName,
            SmtpHost = settings.SmtpHost,
            SmtpPort = settings.SmtpPort,
            SmtpUseStartTls = settings.SmtpUseStartTls,
            SmtpUsername = settings.SmtpUsername ?? string.Empty,
            HasSmtpPassword = !string.IsNullOrWhiteSpace(settings.SmtpPasswordSecretReference),
            GraphTenantId = settings.GraphTenantId,
            GraphClientId = settings.GraphClientId,
            HasGraphClientSecret = !string.IsNullOrWhiteSpace(settings.GraphClientSecretReference),
            GraphAuthority = settings.GraphAuthority,
            GraphBaseUrl = settings.GraphBaseUrl,
            GraphSaveToSentItems = settings.GraphSaveToSentItems
        };
    }

    public async Task SaveMessagingSettingsAsync(
        SecurityMessagingSettingsEditor editor,
        CancellationToken cancellationToken = default)
    {
        var existing = await messagingEmailSettingsStore.GetAsync(cancellationToken);
        var provider = editor.IsEnabled ? editor.Provider : MessagingEmailProvider.Disabled;
        var now = DateTimeOffset.UtcNow;
        var smtpPasswordSecretReference = await ResolveMessagingSecretReferenceAsync(
            existing.SmtpPasswordSecretReference,
            editor.SmtpPassword,
            "messaging:smtp-password",
            provider == MessagingEmailProvider.Smtp,
            cancellationToken);
        var graphClientSecretReference = await ResolveMessagingSecretReferenceAsync(
            existing.GraphClientSecretReference,
            editor.GraphClientSecret,
            "messaging:graph-client-secret",
            provider == MessagingEmailProvider.MicrosoftGraph,
            cancellationToken);

        var settings = existing with
        {
            IsEnabled = editor.IsEnabled,
            Provider = provider,
            SenderAddress = NormalizeOptional(editor.SenderAddress),
            SenderDisplayName = string.IsNullOrWhiteSpace(editor.SenderDisplayName)
                ? "Linux Made Sane"
                : editor.SenderDisplayName.Trim(),
            SmtpHost = NormalizeOptional(editor.SmtpHost),
            SmtpPort = Math.Clamp(editor.SmtpPort, 1, 65535),
            SmtpUseStartTls = editor.SmtpUseStartTls,
            SmtpUsername = NormalizeOptional(editor.SmtpUsername),
            SmtpPasswordSecretReference = smtpPasswordSecretReference,
            GraphTenantId = NormalizeOptional(editor.GraphTenantId),
            GraphClientId = NormalizeOptional(editor.GraphClientId),
            GraphClientSecretReference = graphClientSecretReference,
            GraphAuthority = NormalizeAbsoluteUrlOrDefault(editor.GraphAuthority, "https://login.microsoftonline.com/"),
            GraphBaseUrl = NormalizeAbsoluteUrlOrDefault(editor.GraphBaseUrl, "https://graph.microsoft.com/v1.0"),
            GraphSaveToSentItems = editor.GraphSaveToSentItems,
            LastVerifiedAtUtc = null,
            UpdatedAtUtc = now
        };

        ValidateMessagingSettings(settings);
        await messagingEmailSettingsStore.SaveAsync(settings, cancellationToken);
    }

    public async Task<SecurityMessagingTestResult> SendMessagingTestAsync(
        string recipientAddress,
        CancellationToken cancellationToken = default)
    {
        var result = await emailDeliveryService.SendHtmlAsync(
            recipientAddress,
            "Linux Made Sane email test",
            """
            <p>Linux Made Sane email delivery is configured and working.</p>
            <p>This message was sent from the LMS Security messaging settings test.</p>
            """,
            cancellationToken);

        if (result.Succeeded)
        {
            var settings = await messagingEmailSettingsStore.GetAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            await messagingEmailSettingsStore.SaveAsync(settings with
            {
                LastVerifiedAtUtc = now,
                UpdatedAtUtc = now
            }, cancellationToken);
        }

        return new SecurityMessagingTestResult(result.Succeeded, result.Attempted, result.Message);
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

    private static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeAbsoluteUrlOrDefault(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Enter a valid absolute URL for {candidate}.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private async Task<string?> ResolveMessagingSecretReferenceAsync(
        string? existingSecretReference,
        string? newSecretValue,
        string purpose,
        bool keepForActiveProvider,
        CancellationToken cancellationToken)
    {
        if (!keepForActiveProvider)
        {
            if (!string.IsNullOrWhiteSpace(existingSecretReference))
            {
                await secretStore.DeleteSecretAsync(existingSecretReference, cancellationToken);
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(newSecretValue))
        {
            return existingSecretReference;
        }

        var nextReference = await secretStore.StoreSecretAsync(newSecretValue.Trim(), purpose, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingSecretReference))
        {
            await secretStore.DeleteSecretAsync(existingSecretReference, cancellationToken);
        }

        return nextReference;
    }

    private static void ValidateMessagingSettings(MessagingEmailSettings settings)
    {
        if (!settings.IsEnabled || settings.Provider == MessagingEmailProvider.Disabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.SenderAddress))
        {
            throw new InvalidOperationException("Sender email address is required.");
        }

        if (settings.Provider == MessagingEmailProvider.Smtp)
        {
            if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            {
                throw new InvalidOperationException("SMTP host is required.");
            }

            return;
        }

        if (settings.Provider == MessagingEmailProvider.MicrosoftGraph &&
            (string.IsNullOrWhiteSpace(settings.GraphTenantId) ||
             string.IsNullOrWhiteSpace(settings.GraphClientId) ||
             string.IsNullOrWhiteSpace(settings.GraphClientSecretReference)))
        {
            throw new InvalidOperationException("Microsoft Graph needs tenant id, client id, and client secret.");
        }
    }

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
        bool provisionLocalAccount,
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

            if (!provisionLocalAccount)
            {
                return (linuxUsername, false);
            }

            var isLocalAccountManaged = await remoteAccessSystemService.EnsureLocalAccountAsync(linuxUsername, cancellationToken);
            return (linuxUsername, isLocalAccountManaged);
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

            if (!provisionLocalAccount)
            {
                return (candidate, false);
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

    private async Task<SecurityUserProvisioningViewModel> BuildProvisioningResultAsync(
        SecurityUser user,
        string secret,
        string? lmsLoginUrl,
        LoginSetupEmailKind emailKind,
        bool sendEmail,
        CancellationToken cancellationToken)
    {
        var manualEntryKey = TotpAuthenticator.FormatManualEntryKey(secret);
        var authenticatorIssuer = BuildAuthenticatorIssuer(lmsLoginUrl);
        var otpUri = TotpAuthenticator.BuildOtpUri(user.Email, secret, authenticatorIssuer);
        var emailResult = sendEmail
            ? await SendLoginSetupEmailIfAvailableAsync(
                user,
                manualEntryKey,
                otpUri,
                lmsLoginUrl,
                emailKind,
                cancellationToken)
            : new EmailDeliveryResult(false, false, "Setup QR is ready on this screen.");

        return new SecurityUserProvisioningViewModel(
            user.Id,
            user.Email,
            ResolveLinuxUsername(user),
            user.SshAuthenticationMode,
            manualEntryKey,
            otpUri,
            emailResult.Attempted,
            emailResult.Succeeded,
            emailResult.Message);
    }

    private async Task<EmailDeliveryResult> SendLoginSetupEmailIfAvailableAsync(
        SecurityUser user,
        string manualEntryKey,
        string otpUri,
        string? lmsLoginUrl,
        LoginSetupEmailKind emailKind,
        CancellationToken cancellationToken)
    {
        var settings = await messagingEmailSettingsStore.GetAsync(cancellationToken);
        if (!CanSendLoginSetupEmail(settings))
        {
            return new EmailDeliveryResult(false, false, "Messaging is not enabled and verified.");
        }

        try
        {
            var subject = emailKind == LoginSetupEmailKind.Reset
                ? "Your Linux Made Sane MFA code was reset"
                : "Your Linux Made Sane login is ready";
            var html = BuildLoginSetupEmailHtml(user, manualEntryKey, otpUri, lmsLoginUrl, emailKind);
            return await emailDeliveryService.SendHtmlAsync(user.Email, subject, html, cancellationToken);
        }
        catch (Exception exception)
        {
            return new EmailDeliveryResult(false, true, $"Setup email failed: {exception.Message}");
        }
    }

    private static string BuildLoginSetupEmailHtml(
        SecurityUser user,
        string manualEntryKey,
        string otpUri,
        string? lmsLoginUrl,
        LoginSetupEmailKind emailKind)
    {
        var loginUrl = NormalizeLoginUrl(lmsLoginUrl);
        var qrCodeDataUri = BuildQrCodeDataUri(otpUri);
        var heading = emailKind == LoginSetupEmailKind.Reset
            ? "Your LMS MFA code was reset"
            : "Your LMS login is ready";
        var intro = emailKind == LoginSetupEmailKind.Reset
            ? "Your authenticator setup has been rotated. Scan the new QR code below and remove the old LMS entry from your authenticator app."
            : "An LMS account has been created for you. Scan the QR code below with your authenticator app, then open Linux Made Sane and sign in with your email and MFA code.";

        var encodedEmail = WebUtility.HtmlEncode(user.Email);
        var encodedLinuxUsername = WebUtility.HtmlEncode(ResolveLinuxUsername(user));
        var encodedManualEntryKey = WebUtility.HtmlEncode(manualEntryKey);
        var encodedLoginUrl = WebUtility.HtmlEncode(loginUrl);
        var encodedQrCodeDataUri = WebUtility.HtmlEncode(qrCodeDataUri);

        return $$"""
            <!doctype html>
            <html lang="en">
            <body style="margin:0;padding:0;background:#f4f7fb;font-family:Inter,Segoe UI,Arial,sans-serif;color:#142033;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f4f7fb;padding:28px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:640px;background:#ffffff;border:1px solid #dbe4ef;border-radius:18px;overflow:hidden;">
                      <tr>
                        <td style="background:#07111f;padding:28px 32px;color:#ffffff;">
                          <div style="font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:#94f0c4;font-weight:700;">Linux Made Sane</div>
                          <h1 style="margin:10px 0 0;font-size:28px;line-height:1.2;font-weight:800;">{{WebUtility.HtmlEncode(heading)}}</h1>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:30px 32px 18px;">
                          <p style="margin:0 0 18px;font-size:16px;line-height:1.55;color:#314158;">{{WebUtility.HtmlEncode(intro)}}</p>
                          <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
                            <tr>
                              <td style="width:236px;vertical-align:top;padding:0 24px 20px 0;">
                                <div style="background:#ffffff;border:1px solid #d7e0ec;border-radius:16px;padding:14px;text-align:center;">
                                  <img src="{{encodedQrCodeDataUri}}" width="208" height="208" alt="Linux Made Sane authenticator QR code" style="display:block;width:208px;height:208px;border:0;" />
                                </div>
                              </td>
                              <td style="vertical-align:top;padding:0 0 20px;">
                                <p style="margin:0 0 6px;font-size:13px;color:#607089;font-weight:700;text-transform:uppercase;">Account</p>
                                <p style="margin:0 0 16px;font-size:16px;color:#142033;font-weight:700;">{{encodedEmail}}</p>
                                <p style="margin:0 0 6px;font-size:13px;color:#607089;font-weight:700;text-transform:uppercase;">Linux runner</p>
                                <p style="margin:0 0 16px;font-size:16px;color:#142033;font-weight:700;">{{encodedLinuxUsername}}</p>
                                <p style="margin:0 0 8px;font-size:13px;color:#607089;font-weight:700;text-transform:uppercase;">Manual key</p>
                                <p style="margin:0 0 18px;padding:12px;border-radius:10px;background:#edf3fa;color:#142033;font-family:Consolas,Menlo,monospace;font-size:14px;line-height:1.4;word-break:break-all;">{{encodedManualEntryKey}}</p>
                                <a href="{{encodedLoginUrl}}" style="display:inline-block;background:#0f7b57;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:10px;font-weight:800;">Open Linux Made Sane</a>
                              </td>
                            </tr>
                          </table>
                          <p style="margin:4px 0 0;font-size:13px;line-height:1.5;color:#607089;">This QR code contains your MFA setup secret. Do not forward this email. If you did not request this account change, contact the person who manages your LMS instance.</p>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string BuildQrCodeDataUri(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload.Trim(), QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        return $"data:image/png;base64,{Convert.ToBase64String(qrCode.GetGraphic(8))}";
    }

    private static string NormalizeLoginUrl(string? lmsLoginUrl)
    {
        if (Uri.TryCreate(lmsLoginUrl?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return uri.ToString();
        }

        return "http://localhost:5080/login";
    }

    private static string BuildAuthenticatorIssuer(string? lmsLoginUrl) =>
        $"LMS ({ResolveLmsHostname(lmsLoginUrl)})";

    private static string ResolveLmsHostname(string? lmsLoginUrl)
    {
        if (Uri.TryCreate(lmsLoginUrl?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return "localhost";
    }

    private static SecurityUserViewModel MapUser(SecurityUser user) =>
        new(
            user.Id,
            user.Email,
            ResolveLinuxUsername(user),
            user.IsEnabled,
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes),
            user.SshAuthenticationMode,
            !string.IsNullOrWhiteSpace(user.AuthorizedKeyEntries),
            user.IsLocalAccountManaged,
            !string.IsNullOrWhiteSpace(user.OtpSecretReference),
            user.LastLoginAtUtc,
            user.PasswordChangedAtUtc);

    private static SecurityMessagingSettingsViewModel MapMessaging(MessagingEmailSettings settings) =>
        new(
            settings.IsEnabled,
            settings.Provider,
            settings.SenderAddress,
            settings.SenderDisplayName,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpUseStartTls,
            settings.SmtpUsername ?? string.Empty,
            !string.IsNullOrWhiteSpace(settings.SmtpPasswordSecretReference),
            settings.GraphTenantId,
            settings.GraphClientId,
            !string.IsNullOrWhiteSpace(settings.GraphClientSecretReference),
            settings.GraphAuthority,
            settings.GraphBaseUrl,
            settings.GraphSaveToSentItems,
            settings.LastVerifiedAtUtc,
            CanSendLoginSetupEmail(settings));

    private static bool CanSendLoginSetupEmail(MessagingEmailSettings settings) =>
        settings.IsEnabled &&
        settings.Provider != MessagingEmailProvider.Disabled &&
        settings.LastVerifiedAtUtc.HasValue;

    private enum LoginSetupEmailKind
    {
        Created,
        Reset
    }

    private sealed record InitialSetupBootstrap(
        string SuggestedLinuxUsername,
        string InstallerLinuxUsername,
        string InstallerHomeDirectory)
    {
        public static InitialSetupBootstrap Empty { get; } = new(string.Empty, string.Empty, string.Empty);

        public bool HasInstallerIdentity => !string.IsNullOrWhiteSpace(InstallerLinuxUsername);
    }

    private InitialSetupViewModel BuildInitialSetupViewModel(IReadOnlyList<SecurityUser> users)
    {
        var bootstrap = ReadInitialSetupBootstrap();
        var orderedUsers = users
            .OrderBy(user => user.CreatedAtUtc)
            .ThenBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (orderedUsers.Length == 0)
        {
            return new InitialSetupViewModel(
                false,
                true,
                false,
                null,
                string.Empty,
                string.Empty,
                bootstrap.SuggestedLinuxUsername,
                bootstrap.InstallerLinuxUsername,
                bootstrap.InstallerHomeDirectory,
                bootstrap.HasInstallerIdentity,
                0,
                bootstrap.HasInstallerIdentity
                    ? $"Create the first LMS login and link it to Linux user {bootstrap.InstallerLinuxUsername}."
                    : "Create the first LMS login.");
        }

        var pendingUser = orderedUsers.Length == 1 &&
                          !orderedUsers[0].IsEnabled &&
                          !orderedUsers[0].LastLoginAtUtc.HasValue
            ? orderedUsers[0]
            : null;
        if (pendingUser is not null)
        {
            return new InitialSetupViewModel(
                false,
                false,
                true,
                pendingUser.Id,
                pendingUser.Email,
                ResolveLinuxUsername(pendingUser),
                bootstrap.SuggestedLinuxUsername,
                bootstrap.InstallerLinuxUsername,
                bootstrap.InstallerHomeDirectory,
                bootstrap.HasInstallerIdentity,
                orderedUsers.Length,
                "Verify the first MFA code to finish setup.");
        }

        return new InitialSetupViewModel(
            true,
            false,
            false,
            null,
            string.Empty,
            string.Empty,
            bootstrap.SuggestedLinuxUsername,
            bootstrap.InstallerLinuxUsername,
            bootstrap.InstallerHomeDirectory,
            bootstrap.HasInstallerIdentity,
            orderedUsers.Length,
            "Initial setup is complete.");
    }

    private InitialSetupBootstrap ReadInitialSetupBootstrap()
    {
        if (configuration is null)
        {
            return InitialSetupBootstrap.Empty;
        }

        var section = configuration.GetSection("InitialSetupBootstrap");
        var installerUsername = NormalizeOptional(section["InstallerUsername"]);
        var installerHomeDirectory = NormalizeOptional(section["InstallerHomeDirectory"]);
        var suggestedLinuxUsername = TryNormalizeLinuxUsername(installerUsername);

        return new InitialSetupBootstrap(
            suggestedLinuxUsername,
            suggestedLinuxUsername,
            installerHomeDirectory);
    }

    private static string TryNormalizeLinuxUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var linuxUsername = NormalizeLinuxUsername(value);
            ValidateLinuxUsername(linuxUsername);
            return linuxUsername;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private async Task EnableAuthenticationForBuiltInNetworksAsync(CancellationToken cancellationToken)
    {
        var entries = await trustedNetworkStore.ListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in entries.Where(candidate => candidate.IsBuiltIn))
        {
            await trustedNetworkStore.SaveAsync(entry with
            {
                IsEnabled = true,
                IsTrustedAccessEnabled = false,
                IsAuthenticationEnabled = true,
                UpdatedAtUtc = now
            }, cancellationToken);
        }
    }

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
