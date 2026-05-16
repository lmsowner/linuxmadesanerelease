// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace LinuxMadeSane.Web.Services;

public sealed class PasskeyAuthenticationService(
    ISecurityUserStore userStore,
    ISecurityPasskeyStore passkeyStore,
    IMemoryCache memoryCache,
    ILogger<PasskeyAuthenticationService> logger)
{
    private const string RegistrationStatePrefix = "passkeys:registration:";
    private const string AssertionStatePrefix = "passkeys:assertion:";
    private static readonly TimeSpan FreshOtpEnrollmentWindow = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions PasskeyDeserializeOptions = BuildPasskeyDeserializeOptions();

    public async Task<IReadOnlyList<SecurityPasskeyCredential>> ListForPrincipalAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var user = await ResolvePrincipalUserAsync(principal, cancellationToken);
        return user is null
            ? []
            : await passkeyStore.ListByUserAsync(user.Id, cancellationToken);
    }

    public async Task<bool> ShouldOfferPasskeySetupAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userStore.GetAsync(userId, cancellationToken);
        if (user is null || !user.IsEnabled)
        {
            return false;
        }

        var passkeys = await passkeyStore.ListByUserAsync(user.Id, cancellationToken);
        return passkeys.Count == 0;
    }

    public async Task<PasskeyOperationResult> DeleteAsync(
        ClaimsPrincipal principal,
        Guid passkeyId,
        CancellationToken cancellationToken)
    {
        var user = await ResolvePrincipalUserAsync(principal, cancellationToken);
        if (user is null)
        {
            return new PasskeyOperationResult(false, "You need to be signed in.");
        }

        var passkeys = await passkeyStore.ListByUserAsync(user.Id, cancellationToken);
        if (passkeys.All(passkey => passkey.Id != passkeyId))
        {
            return new PasskeyOperationResult(false, "The passkey was not found for this account.");
        }

        await passkeyStore.DeleteAsync(passkeyId, cancellationToken);
        return new PasskeyOperationResult(true, "Passkey removed.");
    }

    public async Task<PasskeyOptionsResult> BuildAuthenticatedRegistrationOptionsAsync(
        ClaimsPrincipal principal,
        string friendlyName,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var user = await ResolvePrincipalUserAsync(principal, cancellationToken);
        if (user is null || !user.IsEnabled)
        {
            return PasskeyOptionsResult.Fail("Sign in with MFA before setting up a passkey.");
        }

        if (!IsFreshOtpMfaSession(principal))
        {
            return PasskeyOptionsResult.Fail("Sign in with your authenticator code again before setting up a passkey.");
        }

        return await BuildRegistrationOptionsForUserAsync(user, friendlyName, request, cancellationToken);
    }

    private async Task<PasskeyOptionsResult> BuildRegistrationOptionsForUserAsync(
        SecurityUser user,
        string friendlyName,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var fido = BuildFido(request);
        var existingPasskeys = await passkeyStore.ListByUserAsync(user.Id, cancellationToken);
        var fidoUser = new Fido2User
        {
            Id = user.Id.ToByteArray(),
            Name = user.Email,
            DisplayName = user.Email
        };

        var credentialOptions = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = existingPasskeys
                .Select(passkey => new PublicKeyCredentialDescriptor(PasskeyBase64Url.Decode(passkey.CredentialId)))
                .ToArray(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        var stateId = Guid.NewGuid().ToString("N");
        var displayName = string.IsNullOrWhiteSpace(friendlyName)
            ? "Passkey"
            : friendlyName.Trim();

        memoryCache.Set(
            $"{RegistrationStatePrefix}{stateId}",
            new PasskeyRegistrationState(user.Id, displayName, credentialOptions),
            TimeSpan.FromMinutes(5));

        return new PasskeyOptionsResult(true, null, stateId, SerializeCredentialOptions(credentialOptions));
    }

    public async Task<PasskeyOperationResult> CompleteRegistrationAsync(
        ClaimsPrincipal principal,
        string stateId,
        string credentialJson,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!memoryCache.TryGetValue<PasskeyRegistrationState>(
                $"{RegistrationStatePrefix}{stateId}",
                out var state) ||
            state is null)
        {
            return new PasskeyOperationResult(false, "The passkey setup request has expired.");
        }

        var targetUser = await userStore.GetAsync(state.UserId, cancellationToken);
        if (targetUser is null || !targetUser.IsEnabled)
        {
            return new PasskeyOperationResult(false, "The selected LMS account is not available.");
        }

        var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
            credentialJson,
            PasskeyDeserializeOptions);
        if (attestationResponse is null)
        {
            return new PasskeyOperationResult(false, "The passkey response was not valid.");
        }

        try
        {
            var fido = BuildFido(request);
            var result = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = state.Options,
                IsCredentialIdUniqueToUserCallback = async (credentialIdUserParams, token) =>
                {
                    var credentialId = PasskeyBase64Url.Encode(credentialIdUserParams.CredentialId);
                    return !await passkeyStore.CredentialIdExistsAsync(credentialId, token);
                }
            }, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            await passkeyStore.SaveAsync(new SecurityPasskeyCredential(
                Guid.NewGuid(),
                targetUser.Id,
                PasskeyBase64Url.Encode(result.Id),
                PasskeyBase64Url.Encode(result.PublicKey),
                PasskeyBase64Url.Encode(result.User.Id),
                result.SignCount,
                state.FriendlyName,
                result.IsBackedUp,
                now,
                now,
                null), cancellationToken);

            memoryCache.Remove($"{RegistrationStatePrefix}{stateId}");
            return new PasskeyOperationResult(true, "Passkey added.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Passkey registration failed for local LMS user {UserId}.", targetUser.Id);
            return new PasskeyOperationResult(false, "The passkey could not be verified.");
        }
    }

    public async Task<PasskeyOptionsResult> BuildLoginOptionsAsync(
        string email,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userStore.FindByEmailAsync(email, cancellationToken);
        if (user is null || !user.IsEnabled)
        {
            return PasskeyOptionsResult.Fail("No passkey is available for this email.");
        }

        var passkeys = await passkeyStore.ListByUserAsync(user.Id, cancellationToken);
        if (passkeys.Count == 0)
        {
            return PasskeyOptionsResult.Fail("No passkey is available for this email.");
        }

        var fido = BuildFido(request);
        var assertionOptions = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = passkeys
                .Select(passkey => new PublicKeyCredentialDescriptor(PasskeyBase64Url.Decode(passkey.CredentialId)))
                .ToArray(),
            UserVerification = UserVerificationRequirement.Required
        });

        var stateId = Guid.NewGuid().ToString("N");
        memoryCache.Set(
            $"{AssertionStatePrefix}{stateId}",
            new PasskeyAssertionState(user.Id, assertionOptions),
            TimeSpan.FromMinutes(5));

        return new PasskeyOptionsResult(true, null, stateId, SerializeAssertionOptions(assertionOptions));
    }

    public async Task<PasskeyLoginResult> CompleteLoginAsync(
        string stateId,
        string credentialJson,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!memoryCache.TryGetValue<PasskeyAssertionState>(
                $"{AssertionStatePrefix}{stateId}",
                out var state) ||
            state is null)
        {
            return PasskeyLoginResult.Fail("The passkey sign-in request has expired.");
        }

        var assertionResponse = JsonSerializer.Deserialize(
            credentialJson,
            FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse);
        if (assertionResponse is null || string.IsNullOrWhiteSpace(assertionResponse.Id))
        {
            return PasskeyLoginResult.Fail("The passkey response was not valid.");
        }

        var credential = await passkeyStore.GetByCredentialIdAsync(assertionResponse.Id, cancellationToken);
        if (credential is null || credential.UserId != state.UserId)
        {
            return PasskeyLoginResult.Fail("The passkey was not recognised.");
        }

        var user = await userStore.GetAsync(credential.UserId, cancellationToken);
        if (user is null || !user.IsEnabled)
        {
            return PasskeyLoginResult.Fail("The LMS account is disabled.");
        }

        try
        {
            var fido = BuildFido(request);
            var result = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = state.Options,
                StoredPublicKey = PasskeyBase64Url.Decode(credential.PublicKey),
                StoredSignatureCounter = credential.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (credentialIdUserHandleParams, _) =>
                {
                    var credentialId = PasskeyBase64Url.Encode(credentialIdUserHandleParams.CredentialId);
                    var userHandle = PasskeyBase64Url.Encode(credentialIdUserHandleParams.UserHandle);
                    return Task.FromResult(credential.CredentialId == credentialId && credential.UserHandle == userHandle);
                }
            }, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            await passkeyStore.SaveAsync(credential with
            {
                SignatureCounter = result.SignCount,
                IsBackedUp = result.IsBackedUp,
                UpdatedAtUtc = now,
                LastUsedAtUtc = now
            }, cancellationToken);
            await userStore.SaveAsync(user with
            {
                LastLoginAtUtc = now,
                UpdatedAtUtc = now
            }, cancellationToken);

            memoryCache.Remove($"{AssertionStatePrefix}{stateId}");
            return PasskeyLoginResult.Success(user);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Passkey sign-in failed for local LMS user {UserId}.", user.Id);
            return PasskeyLoginResult.Fail("The passkey could not be verified.");
        }
    }

    private async Task<SecurityUser?> ResolvePrincipalUserAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdValue, out var userId)
            ? await userStore.GetAsync(userId, cancellationToken)
            : null;
    }

    private static bool IsFreshOtpMfaSession(ClaimsPrincipal principal)
    {
        if (!principal.HasClaim("amr", "otp"))
        {
            return false;
        }

        var authTimeValue = principal.FindFirstValue("auth_time");
        return long.TryParse(
                   authTimeValue,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var authTimeUnix) &&
               DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authTimeUnix) <= FreshOtpEnrollmentWindow;
    }

    private static Fido2 BuildFido(HttpRequest request)
    {
        var host = request.Host.Host;
        var origin = $"{request.Scheme}://{request.Host}";
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = host,
            ServerName = "Linux Made Sane",
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { origin }
        });
    }

    private static string SerializeCredentialOptions(CredentialCreateOptions credentialOptions) =>
        JsonSerializer.Serialize(credentialOptions, FidoModelSerializerContext.Default.CredentialCreateOptions);

    private static string SerializeAssertionOptions(AssertionOptions assertionOptions) =>
        JsonSerializer.Serialize(assertionOptions, FidoModelSerializerContext.Default.AssertionOptions);

    private static JsonSerializerOptions BuildPasskeyDeserializeOptions() =>
        new(FidoModelSerializerContext.Default.Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                FidoModelSerializerContext.Default,
                new DefaultJsonTypeInfoResolver())
        };

    private sealed record PasskeyRegistrationState(
        Guid UserId,
        string FriendlyName,
        CredentialCreateOptions Options);

    private sealed record PasskeyAssertionState(Guid UserId, AssertionOptions Options);
}

public sealed record PasskeyOperationResult(bool Succeeded, string Message);

public sealed record PasskeyOptionsResult(
    bool Succeeded,
    string? ErrorMessage,
    string? StateId,
    string? OptionsJson)
{
    public static PasskeyOptionsResult Fail(string errorMessage) => new(false, errorMessage, null, null);
}

public sealed record PasskeyLoginResult(
    bool Succeeded,
    string? ErrorMessage,
    SecurityUser? User)
{
    public static PasskeyLoginResult Success(SecurityUser user) => new(true, null, user);
    public static PasskeyLoginResult Fail(string errorMessage) => new(false, errorMessage, null);
}
