using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICloudflareZoneService
{
    Task<IReadOnlyList<CloudflareAccount>> ListAccountsAsync(
        string apiToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(
        string apiToken,
        CancellationToken cancellationToken = default);
}
