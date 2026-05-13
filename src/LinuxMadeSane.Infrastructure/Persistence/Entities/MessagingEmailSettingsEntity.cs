namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MessagingEmailSettingsEntity
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; }
    public int Provider { get; set; }
    public string SenderAddress { get; set; } = string.Empty;
    public string SenderDisplayName { get; set; } = "Linux Made Sane";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordSecretReference { get; set; }
    public string GraphTenantId { get; set; } = string.Empty;
    public string GraphClientId { get; set; } = string.Empty;
    public string? GraphClientSecretReference { get; set; }
    public string GraphAuthority { get; set; } = "https://login.microsoftonline.com/";
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public bool GraphSaveToSentItems { get; set; } = true;
    public DateTimeOffset? LastVerifiedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
