// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Messaging;

public sealed record MessagingEmailSettings(
    int Id,
    bool IsEnabled,
    MessagingEmailProvider Provider,
    string SenderAddress,
    string SenderDisplayName,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseStartTls,
    string? SmtpUsername,
    string? SmtpPasswordSecretReference,
    string GraphTenantId,
    string GraphClientId,
    string? GraphClientSecretReference,
    string GraphAuthority,
    string GraphBaseUrl,
    bool GraphSaveToSentItems,
    DateTimeOffset? LastVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static MessagingEmailSettings CreateDefault(DateTimeOffset now) =>
        new(
            1,
            false,
            MessagingEmailProvider.Disabled,
            string.Empty,
            "Linux Made Sane",
            string.Empty,
            587,
            true,
            string.Empty,
            null,
            string.Empty,
            string.Empty,
            null,
            "https://login.microsoftonline.com/",
            "https://graph.microsoft.com/v1.0",
            true,
            null,
            now,
            now);
}
