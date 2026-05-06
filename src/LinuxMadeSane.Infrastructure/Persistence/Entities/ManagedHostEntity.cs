namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class ManagedHostEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DefaultWorkingDirectory { get; set; } = string.Empty;
    public int OperatingStatus { get; set; }
    public int PrimaryAuthenticationType { get; set; }
    public int? FallbackAuthenticationType { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordSecretReference { get; set; }
    public string? PrivateKeySecretReference { get; set; }
    public string? PrivateKeyPassphraseSecretReference { get; set; }
    public bool UseKeyboardInteractiveFallback { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public int LastConnectionTestStatus { get; set; }
    public string Platform { get; set; } = string.Empty;
    public int Kind { get; set; }

    public List<SavedCommandEntity> SavedCommands { get; set; } = [];
}
