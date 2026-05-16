// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

// Guardrail: user-facing managed-host auth should stay on the standard SSH modes
// operators actually expect: password, public-key auth, or both. Legacy/advanced
// enum values may still be readable for compatibility, but do not surface them as
// the normal selectable path in the shared UI.
public static class ManagedHostAuthenticationPolicy
{
    public static IReadOnlyList<AuthenticationType> OperatorSelectableModes { get; } =
    [
        AuthenticationType.Password,
        AuthenticationType.PasswordAndPrivateKey,
        AuthenticationType.PrivateKey
    ];

    public static bool IsOperatorSelectable(AuthenticationType type) =>
        type is AuthenticationType.Password or AuthenticationType.PasswordAndPrivateKey or AuthenticationType.PrivateKey;

    public static bool UsesPassword(AuthenticationType type) =>
        type is AuthenticationType.Password or AuthenticationType.PasswordAndPrivateKey or AuthenticationType.Conditional;

    public static bool UsesPassword(AuthenticationType? type) =>
        type.HasValue && UsesPassword(type.Value);

    public static bool UsesPrivateKeyMaterial(AuthenticationType type) =>
        type is AuthenticationType.PrivateKey or AuthenticationType.PasswordAndPrivateKey or AuthenticationType.Conditional;

    public static bool UsesPrivateKeyMaterial(AuthenticationType? type) =>
        type.HasValue && UsesPrivateKeyMaterial(type.Value);

    public static bool UsesPublicKeyAuthentication(AuthenticationType type) =>
        UsesPrivateKeyMaterial(type);

    public static bool UsesPublicKeyAuthentication(AuthenticationType? type) =>
        UsesPrivateKeyMaterial(type);

    public static string GetDisplayLabel(AuthenticationType type) => type switch
    {
        AuthenticationType.Password => "Password",
        AuthenticationType.PasswordAndPrivateKey => "Password + public key",
        AuthenticationType.PrivateKey => "Public key",
        AuthenticationType.Agent => "SSH agent (legacy)",
        _ => "Conditional (legacy)"
    };
}
