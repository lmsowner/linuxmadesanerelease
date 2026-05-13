using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityMessagingSettingsViewModel(
    bool IsEnabled,
    MessagingEmailProvider Provider,
    string SenderAddress,
    string SenderDisplayName,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseStartTls,
    string SmtpUsername,
    bool HasSmtpPassword,
    string GraphTenantId,
    string GraphClientId,
    bool HasGraphClientSecret,
    string GraphAuthority,
    string GraphBaseUrl,
    bool GraphSaveToSentItems,
    DateTimeOffset? LastVerifiedAtUtc,
    bool CanSendLoginSetupEmail);
