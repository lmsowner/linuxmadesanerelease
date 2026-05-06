namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class ProtectedSecretEntity
{
    public string Reference { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string ProtectedValue { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
