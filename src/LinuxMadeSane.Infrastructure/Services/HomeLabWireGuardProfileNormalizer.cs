// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Infrastructure.Services;

internal static partial class HomeLabWireGuardProfileNormalizer
{
    public static Task<string> NormalizeAsync(string profile, CancellationToken cancellationToken = default) =>
        NormalizeAsync(profile, Dns.GetHostAddressesAsync, cancellationToken);

    internal static async Task<string> NormalizeAsync(
        string profile,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveEndpointAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentNullException.ThrowIfNull(resolveEndpointAsync);

        var endpointMatches = EndpointLineRegex().Matches(profile);
        if (endpointMatches.Count == 0)
        {
            throw new InvalidOperationException("The WireGuard profile does not contain an Endpoint = host:port line.");
        }

        var output = new StringBuilder(profile.Length + 32);
        var copiedThrough = 0;
        var resolvedAddresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in endpointMatches)
        {
            output.Append(profile, copiedThrough, match.Index - copiedThrough);
            output.Append(match.Groups["prefix"].Value);

            var endpoint = match.Groups["endpoint"].Value;
            var (hostName, port) = ParseEndpoint(endpoint);
            if (IPAddress.TryParse(hostName, out _))
            {
                output.Append(endpoint);
            }
            else
            {
                if (!resolvedAddresses.TryGetValue(hostName, out var address))
                {
                    IPAddress[] addresses;
                    try
                    {
                        addresses = await resolveEndpointAsync(hostName, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            $"The WireGuard endpoint hostname '{hostName}' could not be resolved. Check this server's DNS and internet connection, then try again.",
                            exception);
                    }

                    address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                        ?? addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetworkV6)
                        ?? throw new InvalidOperationException(
                            $"The WireGuard endpoint hostname '{hostName}' did not resolve to an IPv4 or IPv6 address. Check this server's DNS and internet connection, then try again.");
                    resolvedAddresses[hostName] = address;
                }

                output.Append(address.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"[{address}]:{port}"
                    : $"{address}:{port}");
            }

            output.Append(match.Groups["suffix"].Value);
            copiedThrough = match.Index + match.Length;
        }

        output.Append(profile, copiedThrough, profile.Length - copiedThrough);
        return output.ToString();
    }

    private static (string HostName, int Port) ParseEndpoint(string endpoint)
    {
        string hostName;
        string portText;
        if (endpoint[0] == '[')
        {
            var closingBracket = endpoint.IndexOf(']');
            if (closingBracket <= 1 || closingBracket + 1 >= endpoint.Length || endpoint[closingBracket + 1] != ':')
            {
                throw InvalidEndpoint(endpoint);
            }

            hostName = endpoint[1..closingBracket];
            portText = endpoint[(closingBracket + 2)..];
        }
        else
        {
            var separator = endpoint.LastIndexOf(':');
            if (separator <= 0 || separator == endpoint.Length - 1)
            {
                throw InvalidEndpoint(endpoint);
            }

            hostName = endpoint[..separator];
            portText = endpoint[(separator + 1)..];
        }

        if (string.IsNullOrWhiteSpace(hostName) ||
            !int.TryParse(portText, out var port) ||
            port is < 1 or > 65535)
        {
            throw InvalidEndpoint(endpoint);
        }

        return (hostName, port);
    }

    private static InvalidOperationException InvalidEndpoint(string endpoint) =>
        new($"The WireGuard endpoint '{endpoint}' is invalid. Expected Endpoint = host:port.");

    [GeneratedRegex(
        @"^(?<prefix>[ \t]*Endpoint[ \t]*=[ \t]*)(?<endpoint>\S+)(?<suffix>[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EndpointLineRegex();
}
