// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public static class TrustedNetworkMatcher
{
    public static bool IsValidAddressOrCidr(string addressOrCidr)
    {
        try
        {
            _ = ParseNetwork(addressOrCidr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static TrustedNetworkEntry? Match(IPAddress? remoteAddress, IReadOnlyList<TrustedNetworkEntry> entries)
    {
        if (remoteAddress is null)
        {
            return null;
        }

        var normalizedRemote = Normalize(remoteAddress);
        return entries
            .Select(item => new
            {
                Entry = item,
                Network = ParseNetwork(item.AddressOrCidr)
            })
            .Where(item => Contains(item.Network, normalizedRemote))
            .OrderByDescending(item => item.Network.PrefixLength)
            .ThenBy(item => item.Entry.IsBuiltIn)
            .ThenBy(item => item.Entry.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Entry)
            .FirstOrDefault();
    }

    private static bool Contains((IPAddress Address, int PrefixLength) network, IPAddress remoteAddress)
    {
        var networkAddress = Normalize(network.Address);
        if (networkAddress.AddressFamily != remoteAddress.AddressFamily)
        {
            return false;
        }

        var networkBytes = networkAddress.GetAddressBytes();
        var remoteBytes = remoteAddress.GetAddressBytes();
        var fullBytes = network.PrefixLength / 8;
        var remainingBits = network.PrefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (networkBytes[index] != remoteBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = 0xff << (8 - remainingBits);
        return (networkBytes[fullBytes] & mask) == (remoteBytes[fullBytes] & mask);
    }

    private static (IPAddress Address, int PrefixLength) ParseNetwork(string addressOrCidr)
    {
        var normalized = addressOrCidr.Trim();
        var slashIndex = normalized.IndexOf('/');
        if (slashIndex < 0)
        {
            var address = Normalize(IPAddress.Parse(normalized));
            return (address, address.GetAddressBytes().Length * 8);
        }

        var addressPart = normalized[..slashIndex];
        var prefixPart = normalized[(slashIndex + 1)..];
        var parsedAddress = Normalize(IPAddress.Parse(addressPart));
        var maxPrefix = parsedAddress.GetAddressBytes().Length * 8;
        if (!int.TryParse(prefixPart, out var prefixLength) || prefixLength < 0 || prefixLength > maxPrefix)
        {
            throw new InvalidOperationException("The CIDR prefix length is not valid.");
        }

        return (parsedAddress, prefixLength);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
