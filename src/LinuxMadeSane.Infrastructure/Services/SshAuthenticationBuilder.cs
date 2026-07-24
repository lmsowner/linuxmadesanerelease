// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class SshAuthenticationBuilder
{
    public static List<AuthenticationMethod> BuildAuthenticationMethods(
        ManagedHost host,
        ManagedHostSshCredentials credentials)
    {
        var primaryAuthenticationType = credentials.AuthenticationTypeOverride ?? host.PrimaryAuthenticationType;
        var fallbackAuthenticationType = credentials.AuthenticationTypeOverride.HasValue
            ? null
            : host.FallbackAuthenticationType;

        return BuildAuthenticationMethods(
            credentials.Username,
            credentials.Password,
            credentials.PrivateKey,
            credentials.PrivateKeyPassphrase,
            primaryAuthenticationType,
            fallbackAuthenticationType,
            host.UseKeyboardInteractiveFallback ||
            primaryAuthenticationType is AuthenticationType.Conditional or AuthenticationType.PasswordAndPrivateKey ||
            fallbackAuthenticationType is AuthenticationType.Conditional or AuthenticationType.PasswordAndPrivateKey);
    }

    public static List<AuthenticationMethod> BuildAuthenticationMethods(
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        AuthenticationType primaryAuthenticationType,
        AuthenticationType? fallbackAuthenticationType,
        bool useKeyboardInteractiveFallback)
    {
        var methods = new List<AuthenticationMethod>();
        var normalizedUsername = username.Trim();
        var failureMessages = new List<string>();
        var addedPasswordMethod = false;
        var addedKeyboardInteractiveMethod = false;
        var addedPrivateKeyMethod = false;

        foreach (var authenticationType in EnumerateAuthenticationTypes(primaryAuthenticationType, fallbackAuthenticationType))
        {
            if (TryAddAuthenticationMethodsForType(
                    authenticationType,
                    normalizedUsername,
                    password,
                    privateKey,
                    privateKeyPassphrase,
                    useKeyboardInteractiveFallback,
                    methods,
                    ref addedPasswordMethod,
                    ref addedKeyboardInteractiveMethod,
                    ref addedPrivateKeyMethod,
                    out var failureMessage))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(failureMessage))
            {
                failureMessages.Add(failureMessage);
            }
        }

        if (methods.Count > 0)
        {
            return methods;
        }

        if (failureMessages.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", failureMessages.Distinct(StringComparer.Ordinal)));
        }

        return methods;
    }

    private static IEnumerable<AuthenticationType> EnumerateAuthenticationTypes(
        AuthenticationType primaryAuthenticationType,
        AuthenticationType? fallbackAuthenticationType)
    {
        yield return primaryAuthenticationType;

        if (fallbackAuthenticationType.HasValue && fallbackAuthenticationType.Value != primaryAuthenticationType)
        {
            yield return fallbackAuthenticationType.Value;
        }
    }

    private static bool TryAddAuthenticationMethodsForType(
        AuthenticationType authenticationType,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool useKeyboardInteractiveFallback,
        List<AuthenticationMethod> methods,
        ref bool addedPasswordMethod,
        ref bool addedKeyboardInteractiveMethod,
        ref bool addedPrivateKeyMethod,
        out string? failureMessage)
    {
        switch (authenticationType)
        {
            case AuthenticationType.Password:
                if (string.IsNullOrWhiteSpace(password))
                {
                    failureMessage = "A password is required for the selected SSH authentication mode.";
                    return false;
                }

                AddPasswordAuthenticationMethods(
                    username,
                    password,
                    useKeyboardInteractiveFallback,
                    methods,
                    ref addedPasswordMethod,
                    ref addedKeyboardInteractiveMethod);
                failureMessage = null;
                return true;

            case AuthenticationType.PrivateKey:
                return TryAddPrivateKeyAuthenticationMethod(
                    username,
                    privateKey,
                    privateKeyPassphrase,
                    methods,
                    ref addedPrivateKeyMethod,
                    out failureMessage);

            case AuthenticationType.PasswordAndPrivateKey:
                if (string.IsNullOrWhiteSpace(password))
                {
                    failureMessage = "A password is required for the selected SSH authentication mode.";
                    return false;
                }

                if (!TryAddPrivateKeyAuthenticationMethod(
                        username,
                        privateKey,
                        privateKeyPassphrase,
                        methods,
                        ref addedPrivateKeyMethod,
                        out failureMessage))
                {
                    return false;
                }

                AddPasswordAuthenticationMethods(
                    username,
                    password,
                    true,
                    methods,
                    ref addedPasswordMethod,
                    ref addedKeyboardInteractiveMethod);
                failureMessage = null;
                return true;

            case AuthenticationType.Conditional:
            {
                var conditionalFailures = new List<string>();
                var addedConditionalMethod = false;

                if (TryAddPrivateKeyAuthenticationMethod(
                        username,
                        privateKey,
                        privateKeyPassphrase,
                        methods,
                        ref addedPrivateKeyMethod,
                        out var privateKeyFailure))
                {
                    addedConditionalMethod = true;
                }
                else if (!string.IsNullOrWhiteSpace(privateKeyFailure))
                {
                    conditionalFailures.Add(privateKeyFailure);
                }

                if (!string.IsNullOrWhiteSpace(password))
                {
                    AddPasswordAuthenticationMethods(
                        username,
                        password,
                        true,
                        methods,
                        ref addedPasswordMethod,
                        ref addedKeyboardInteractiveMethod);
                    addedConditionalMethod = true;
                }

                if (addedConditionalMethod)
                {
                    failureMessage = null;
                    return true;
                }

                failureMessage = conditionalFailures.Count > 0
                    ? string.Join(" ", conditionalFailures.Distinct(StringComparer.Ordinal))
                    : "Provide a password or private key for SSH authentication.";
                return false;
            }

            case AuthenticationType.Agent:
                failureMessage = "SSH agent authentication is not supported yet.";
                return false;

            default:
                failureMessage = "The selected SSH authentication mode is not supported.";
                return false;
        }
    }

    private static void AddPasswordAuthenticationMethods(
        string username,
        string password,
        bool useKeyboardInteractiveFallback,
        List<AuthenticationMethod> methods,
        ref bool addedPasswordMethod,
        ref bool addedKeyboardInteractiveMethod)
    {
        if (!addedPasswordMethod)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password));
            addedPasswordMethod = true;
        }

        if (!useKeyboardInteractiveFallback || addedKeyboardInteractiveMethod)
        {
            return;
        }

        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(username);
        keyboardInteractive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = password;
                }
            }
        };

        methods.Add(keyboardInteractive);
        addedKeyboardInteractiveMethod = true;
    }

    private static bool TryAddPrivateKeyAuthenticationMethod(
        string username,
        string? privateKey,
        string? privateKeyPassphrase,
        List<AuthenticationMethod> methods,
        ref bool addedPrivateKeyMethod,
        out string? failureMessage)
    {
        if (addedPrivateKeyMethod)
        {
            failureMessage = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            failureMessage = "A private key is required for the selected SSH authentication mode.";
            return false;
        }

        var normalizedPrivateKey = privateKey.Trim();
        if (SshKeyMaterialClassifier.LooksLikePublicKey(normalizedPrivateKey))
        {
            failureMessage = "The supplied SSH key is a public key. Paste the private key instead.";
            return false;
        }

        try
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(normalizedPrivateKey));
            var privateKeyFile = string.IsNullOrWhiteSpace(privateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, privateKeyPassphrase);

            methods.Add(new PrivateKeyAuthenticationMethod(username, privateKeyFile));
            addedPrivateKeyMethod = true;
            failureMessage = null;
            return true;
        }
        catch (Exception exception) when (exception is SshException or InvalidOperationException or FormatException or ArgumentException)
        {
            failureMessage = BuildPrivateKeyFailureMessage(exception);
            return false;
        }
    }

    private static string BuildPrivateKeyFailureMessage(Exception exception)
    {
        if (exception.Message.Contains("PUBLIC KEY", StringComparison.OrdinalIgnoreCase))
        {
            return "The supplied SSH key is a public key. Paste the private key instead.";
        }

        return $"The supplied SSH private key is not valid: {exception.Message}";
    }
}
