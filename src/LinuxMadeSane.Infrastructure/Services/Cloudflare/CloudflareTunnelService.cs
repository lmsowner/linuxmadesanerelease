using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

public sealed class CloudflareTunnelService(
    ICloudflareClient client,
    IOptions<CloudflareIntegrationOptions> options) : ICloudflareTunnelService
{
    private readonly CloudflareIntegrationOptions integrationOptions = options.Value;

    public async Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        string apiToken,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareTunnelDto>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel(integrationOptions.ManagedTunnelNamePrefix))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudflareTunnel> CreateTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelName,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PostAsync<object, CloudflareTunnelDto>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel",
            new
            {
                name = tunnelName,
                config_src = "cloudflare"
            },
            cancellationToken);

        return result.ToModel(integrationOptions.ManagedTunnelNamePrefix);
    }

    public async Task<CloudflareTunnelConfiguration> GetConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await client.GetAsync<CloudflareTunnelConfigurationResponseDto>(
                apiToken,
                $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
                cancellationToken: cancellationToken);

            return result.ToModel();
        }
        catch (CloudflareApiException exception) when (exception.StatusCode == 404)
        {
            return new CloudflareTunnelConfiguration([]);
        }
    }

    public Task UpdateConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CloudflareTunnelConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var ingress = configuration.Routes
            .Select(route => string.IsNullOrWhiteSpace(route.Hostname)
                ? new Dictionary<string, object?>
                {
                    ["service"] = route.Service
                }
                : new Dictionary<string, object?>
                {
                    ["hostname"] = route.Hostname,
                    ["service"] = route.Service,
                    ["originRequest"] = new { }
                })
            .ToArray();

        return client.PutAsync<object, CloudflareTunnelConfigurationResponseDto>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
            new
            {
                config = new
                {
                    ingress
                }
            },
            cancellationToken);
    }

    public Task<string> GetTunnelTokenAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default) =>
        client.GetAsync<string>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel/{tunnelId}/token",
            cancellationToken: cancellationToken);

    public Task DeleteTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"accounts/{accountId}/cfd_tunnel/{tunnelId}", cancellationToken);
}
