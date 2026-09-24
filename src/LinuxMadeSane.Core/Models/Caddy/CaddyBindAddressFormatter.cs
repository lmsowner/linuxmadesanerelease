// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;

namespace LinuxMadeSane.Core.Models.Caddy;

public static class CaddyBindAddressFormatter
{
    public static IReadOnlyList<string> NormalizeMany(string value)
    {
        var addresses = Tokenize(value)
            .Select(NormalizeOne)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (addresses.Length == 0)
        {
            return ["127.0.0.1"];
        }

        var anyAddress = addresses.FirstOrDefault(IsAnyAddress);
        return string.IsNullOrWhiteSpace(anyAddress)
            ? addresses
            : [anyAddress];
    }

    public static string NormalizeCsv(string value) =>
        string.Join(", ", NormalizeMany(value));

    public static string ToCaddyBindArguments(string value) =>
        string.Join(' ', NormalizeMany(value));

    public static bool IsAnyAddressList(string value) =>
        NormalizeMany(value).Any(IsAnyAddress);

    public static string FormatEndpointLabel(string value, int port)
    {
        var addresses = NormalizeMany(value)
            .Select(FormatHostForEndpointLabel)
            .ToArray();

        return $"{string.Join(", ", addresses)}:{Math.Clamp(port, 1, 65535)}";
    }

    public static string FormatHostForEndpointLabel(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return IPAddress.TryParse(normalized, out var address) &&
               address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : normalized;
    }

    private static IEnumerable<string> Tokenize(string value) =>
        (value ?? string.Empty)
        .Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeOne(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "127.0.0.1";
        }

        if (normalized == "*")
        {
            return "0.0.0.0";
        }

        if (IPAddress.TryParse(normalized, out var address))
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        }

        throw new InvalidOperationException("Select a local source interface IP, loopback, or all interfaces.");
    }

    private static bool IsAnyAddress(string value) =>
        value.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("::", StringComparison.OrdinalIgnoreCase);
}
