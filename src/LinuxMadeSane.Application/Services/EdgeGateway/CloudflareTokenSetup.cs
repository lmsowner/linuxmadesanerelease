// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public static class CloudflareTokenSetup
{
    // Cloudflare user-token templates accept short keys, not permission-group IDs.
    // Verified in the live dashboard: Zone Read + DNS Write; Cloudflare Tunnel Write + Account Settings Read.
    public static string CreateTokenUrl { get; } = "https://dash.cloudflare.com/profile/api-tokens?permissionGroupKeys=" +
        Uri.EscapeDataString(JsonSerializer.Serialize(new[]
        {
            new { key = "argotunnel", type = "edit" },
            new { key = "account_settings", type = "read" },
            new { key = "zone", type = "read" },
            new { key = "dns", type = "edit" }
        })) + "&accountId=%2A&zoneId=all&name=Linux%20Made%20Sane";

    public static string NormalizeToken(string input)
    {
        var token = input.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        if (token.Length == 0 || token.Length > 4096 || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Paste only the Cloudflare API token value, without commands, quotes or extra text.");
        if (token.StartsWith("cfk_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is a Global API Key. Use Create a token for LMS to create a scoped API token instead.");
        // Cloudflare supports legacy tokens and the new cfut_/cfat_ formats. Do not assume a fixed length.
        return token;
    }
}
