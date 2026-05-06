namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class TrustedNetworkEntryEntity
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string AddressOrCidr { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsTrustedAccessEnabled { get; set; }
    public bool IsAuthenticationEnabled { get; set; }
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
