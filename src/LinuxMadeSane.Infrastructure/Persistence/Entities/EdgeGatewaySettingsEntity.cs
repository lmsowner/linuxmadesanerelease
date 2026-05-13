namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class EdgeGatewaySettingsEntity
{
    public int Id { get; set; }
    public string GatewaySubdomain { get; set; } = "relay";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
