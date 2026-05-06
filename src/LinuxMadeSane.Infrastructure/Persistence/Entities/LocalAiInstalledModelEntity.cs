namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalAiInstalledModelEntity
{
    public string ModelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Digest { get; set; } = string.Empty;
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public bool IsRunning { get; set; }
    public bool IsDefault { get; set; }
    public int Capabilities { get; set; }
    public string Detail { get; set; } = string.Empty;
}
