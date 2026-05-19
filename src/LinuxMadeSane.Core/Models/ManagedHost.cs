// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record ManagedHost(
    Guid Id,
    string Name,
    string Hostname,
    int Port,
    string Environment,
    string Description,
    string DefaultWorkingDirectory,
    HostOperatingStatus OperatingStatus,
    AuthenticationType PrimaryAuthenticationType,
    AuthenticationType? FallbackAuthenticationType,
    string Username,
    string? PasswordSecretReference,
    string? PrivateKeySecretReference,
    string? PrivateKeyPassphraseSecretReference,
    bool UseKeyboardInteractiveFallback,
    DateTimeOffset? LastSeenUtc,
    ConnectionTestStatus LastConnectionTestStatus,
    string Platform,
    ManagedHostKind Kind = ManagedHostKind.SshHost);
