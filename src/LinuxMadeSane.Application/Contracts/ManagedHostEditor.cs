using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts;

public sealed class ManagedHostEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Hostname { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 22;

    public string Environment { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    [Required]
    public string DefaultWorkingDirectory { get; set; } = "/home";

    public HostOperatingStatus OperatingStatus { get; set; } = HostOperatingStatus.Unknown;

    public AuthenticationType PrimaryAuthenticationType { get; set; } = AuthenticationType.Password;

    public AuthenticationType? FallbackAuthenticationType { get; set; }

    [Required]
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool HasStoredPassword { get; set; }

    public bool ClearStoredPassword { get; set; }

    public string PrivateKey { get; set; } = string.Empty;

    public bool HasStoredPrivateKey { get; set; }

    public bool ClearStoredPrivateKey { get; set; }

    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    public bool HasStoredPrivateKeyPassphrase { get; set; }

    public bool ClearStoredPrivateKeyPassphrase { get; set; }

    public bool UseKeyboardInteractiveFallback { get; set; }

    public string Platform { get; set; } = string.Empty;

    public ManagedHostKind HostKind { get; set; } = ManagedHostKind.SshHost;
}
