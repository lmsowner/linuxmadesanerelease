using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

internal sealed class CloudflareEnvelope<TResult>
{
    public bool Success { get; set; }
    public TResult? Result { get; set; }
    public List<CloudflareMessageDto> Errors { get; set; } = [];
    public List<CloudflareMessageDto> Messages { get; set; } = [];

    [JsonPropertyName("result_info")]
    public CloudflareResultInfoDto? ResultInfo { get; set; }
}

internal sealed class CloudflareMessageDto
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("documentation_url")]
    public string? DocumentationUrl { get; set; }

    public CloudflareSourceDto? Source { get; set; }
}

internal sealed class CloudflareSourceDto
{
    public string? Pointer { get; set; }
}

internal sealed class CloudflareResultInfoDto
{
    public int Page { get; set; }

    [JsonPropertyName("per_page")]
    public int PerPage { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    public int Count { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

internal sealed class CloudflareAccountDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

internal sealed class CloudflareZoneDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Paused { get; set; }
    public CloudflareZoneAccountDto? Account { get; set; }
}

internal sealed class CloudflareZoneAccountDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal sealed class CloudflareDnsRecordDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Proxied { get; set; }
    public int Ttl { get; set; }
    public string? Comment { get; set; }

    [JsonPropertyName("modified_on")]
    public DateTimeOffset? ModifiedOn { get; set; }
}

internal sealed class CloudflareTunnelDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("account_tag")]
    public string AccountTag { get; set; } = string.Empty;

    [JsonPropertyName("config_src")]
    public string ConfigSrc { get; set; } = string.Empty;

    [JsonPropertyName("deleted_at")]
    public DateTimeOffset? DeletedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }
}

internal sealed class CloudflareTunnelConfigurationResponseDto
{
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    public CloudflareTunnelConfigDto? Config { get; set; }
}

internal sealed class CloudflareTunnelConfigDto
{
    public List<CloudflareTunnelIngressDto> Ingress { get; set; } = [];
}

internal sealed class CloudflareTunnelIngressDto
{
    public string? Hostname { get; set; }
    public string Service { get; set; } = string.Empty;
    public CloudflareTunnelOriginRequestDto? OriginRequest { get; set; }
}

internal sealed class CloudflareTunnelOriginRequestDto
{
    [JsonPropertyName("noTLSVerify")]
    public bool? NoTlsVerify { get; set; }

    public string? OriginServerName { get; set; }

    [JsonPropertyName("matchSNItoHost")]
    public bool? MatchSniToHost { get; set; }

    public string? CaPool { get; set; }
    public int? TlsTimeout { get; set; }
    public bool? Http2Origin { get; set; }
    public string? HttpHostHeader { get; set; }
    public bool? DisableChunkedEncoding { get; set; }
    public int? ConnectTimeout { get; set; }
    public bool? NoHappyEyeballs { get; set; }
    public string? ProxyType { get; set; }
    public int? KeepAliveTimeout { get; set; }
    public int? KeepAliveConnections { get; set; }
    public int? TcpKeepAlive { get; set; }
}

internal sealed class CloudflareAccessApplicationDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Aud { get; set; }

    [JsonPropertyName("session_duration")]
    public string? SessionDuration { get; set; }

    public JsonElement? Destinations { get; set; }
}

internal sealed class CloudflareAccessPolicyDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public JsonElement? Include { get; set; }
}

internal static class CloudflareMappings
{
    public static CloudflareAccount ToModel(this CloudflareAccountDto dto) =>
        new(dto.Id, dto.Name, dto.Type, null);

    public static CloudflareZone ToModel(this CloudflareZoneDto dto) =>
        new(
            dto.Id,
            dto.Name,
            dto.Account?.Id ?? string.Empty,
            dto.Account?.Name ?? string.Empty,
            dto.Status,
            dto.Paused);

    public static CloudflareDnsRecord ToModel(this CloudflareDnsRecordDto dto, string zoneId) =>
        new(
            dto.Id,
            zoneId,
            dto.Name,
            dto.Type,
            dto.Content,
            dto.Proxied,
            dto.Ttl,
            dto.Comment ?? string.Empty,
            dto.ModifiedOn);

    public static CloudflareTunnel ToModel(this CloudflareTunnelDto dto, string managedPrefix) =>
        new(
            dto.Id,
            dto.AccountTag,
            dto.Name,
            dto.ConfigSrc,
            dto.Status,
            dto.DeletedAt.HasValue,
            dto.Name.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase),
            dto.CreatedAt ?? DateTimeOffset.UtcNow);

    public static CloudflareTunnelConfiguration ToModel(this CloudflareTunnelConfigurationResponseDto dto)
    {
        var routes = dto.Config?.Ingress
            .Where(item => !string.IsNullOrWhiteSpace(item.Service))
            .Select(item => new CloudflareTunnelRoute(
                item.Hostname ?? string.Empty,
                item.Service,
                item.OriginRequest?.ToModel()))
            .ToArray()
            ?? [];

        return new CloudflareTunnelConfiguration(routes);
    }

    private static CloudflareOriginRequestSettings ToModel(this CloudflareTunnelOriginRequestDto dto) =>
        new(
            dto.OriginServerName ?? string.Empty,
            dto.CaPool ?? string.Empty,
            dto.NoTlsVerify == true,
            dto.TlsTimeout ?? 10,
            dto.Http2Origin == true,
            dto.MatchSniToHost == true,
            dto.HttpHostHeader ?? string.Empty,
            dto.DisableChunkedEncoding == true,
            dto.ConnectTimeout ?? 30,
            dto.NoHappyEyeballs == true,
            dto.ProxyType ?? string.Empty,
            dto.KeepAliveTimeout ?? 90,
            dto.KeepAliveConnections ?? 100,
            dto.TcpKeepAlive ?? 30);

    public static CloudflareAccessApplication ToModel(this CloudflareAccessApplicationDto dto, string accountId)
    {
        var domain = dto.Domain;
        if (string.IsNullOrWhiteSpace(domain) && dto.Destinations is { ValueKind: JsonValueKind.Array } destinations)
        {
            foreach (var item in destinations.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    item.TryGetProperty("uri", out var uri) &&
                    string.Equals(type.GetString(), "public", StringComparison.OrdinalIgnoreCase))
                {
                    domain = uri.GetString();
                    break;
                }
            }
        }

        return new CloudflareAccessApplication(
            dto.Id,
            accountId,
            dto.Name,
            domain ?? string.Empty,
            dto.Type,
            dto.Aud ?? string.Empty,
            dto.SessionDuration ?? string.Empty);
    }

    public static CloudflareAccessPolicy ToModel(this CloudflareAccessPolicyDto dto)
    {
        var emails = new List<string>();
        var domains = new List<string>();

        if (dto.Include is { ValueKind: JsonValueKind.Array } include)
        {
            foreach (var item in include.EnumerateArray())
            {
                TryReadValue(item, "email", "email", emails);
                TryReadValue(item, "email_domain", "domain", domains);
            }
        }

        return new CloudflareAccessPolicy(dto.Id, string.Empty, dto.Name, dto.Decision, emails, domains);
    }

    private static void TryReadValue(JsonElement element, string propertyName, string nestedPropertyName, ICollection<string> destination)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var item = value.GetString();
            if (!string.IsNullOrWhiteSpace(item))
            {
                destination.Add(item);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(nestedPropertyName, out var nested) &&
            nested.ValueKind == JsonValueKind.String)
        {
            var item = nested.GetString();
            if (!string.IsNullOrWhiteSpace(item))
            {
                destination.Add(item);
            }
        }
    }
}
