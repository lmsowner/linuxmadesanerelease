// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class HomeLabQbittorrentPortForwarding
{
    public static bool TryReadSettings(string json, out HomeLabQbittorrentPortSettings settings)
    {
        settings = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("listen_port", out var portElement) ||
                !portElement.TryGetInt32(out var listenPort) ||
                !root.TryGetProperty("current_network_interface", out var interfaceElement) ||
                interfaceElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("upnp", out var upnpElement) ||
                upnpElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            settings = new HomeLabQbittorrentPortSettings(
                listenPort,
                interfaceElement.GetString() ?? string.Empty,
                upnpElement.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool Matches(HomeLabQbittorrentPortSettings settings, int forwardedPort) =>
        settings.ListenPort == forwardedPort &&
        settings.NetworkInterface.Equals("tun0", StringComparison.OrdinalIgnoreCase) &&
        !settings.UpnpEnabled;

    public static string BuildPreferencesFormValue(int forwardedPort) =>
        $"json={JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["listen_port"] = forwardedPort,
            ["current_network_interface"] = "tun0",
            ["random_port"] = false,
            ["upnp"] = false
        })}";
}

internal readonly record struct HomeLabQbittorrentPortSettings(
    int ListenPort,
    string NetworkInterface,
    bool UpnpEnabled);
