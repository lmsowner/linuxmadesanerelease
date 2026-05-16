// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class SecurityMessagingSettingsEditor
{
    public bool IsEnabled { get; set; }
    public MessagingEmailProvider Provider { get; set; } = MessagingEmailProvider.Disabled;
    public string SenderAddress { get; set; } = string.Empty;
    public string SenderDisplayName { get; set; } = "Linux Made Sane";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public bool HasSmtpPassword { get; set; }
    public string GraphTenantId { get; set; } = string.Empty;
    public string GraphClientId { get; set; } = string.Empty;
    public string GraphClientSecret { get; set; } = string.Empty;
    public bool HasGraphClientSecret { get; set; }
    public string GraphAuthority { get; set; } = "https://login.microsoftonline.com/";
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public bool GraphSaveToSentItems { get; set; } = true;
}
