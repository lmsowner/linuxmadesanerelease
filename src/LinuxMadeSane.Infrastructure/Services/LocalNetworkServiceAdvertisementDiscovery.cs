// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed record LocalHttpServiceAdvertisement(
    string Host,
    IPAddress? Address,
    int Port,
    string Scope,
    string? DisplayName);

internal static class LocalNetworkServiceAdvertisementDiscovery
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(1250);

    public static async Task<IReadOnlyList<LocalHttpServiceAdvertisement>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            DiscoverSsdpAsync(cancellationToken),
            DiscoverMdnsAsync(cancellationToken),
            DiscoverWsDiscoveryAsync(cancellationToken));

        return results
            .SelectMany(items => items)
            .Where(item => item.Port is > 0 and <= 65535)
            .DistinctBy(item => $"{item.Scope}|{item.Host}|{item.Port}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<LocalHttpServiceAdvertisement>> DiscoverSsdpAsync(CancellationToken cancellationToken)
    {
        const string request = "M-SEARCH * HTTP/1.1\r\n" +
                               "HOST: 239.255.255.250:1900\r\n" +
                               "MAN: \"ssdp:discover\"\r\n" +
                               "MX: 1\r\n" +
                               "ST: ssdp:all\r\n" +
                               "USER-AGENT: LinuxMadeSane/1.0\r\n\r\n";

        try
        {
            using var client = CreateUdpClient();
            var payload = Encoding.ASCII.GetBytes(request);
            await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
            var responses = await ReceiveAsync(client, cancellationToken);
            return responses
                .Select(response => ParseHeader(response.Buffer, "LOCATION", "SERVER"))
                .Where(item => item is not null)
                .Select(item => BuildFromUri(item!.Value.Location, "SSDP", item.Value.DisplayName))
                .Where(item => item is not null)
                .Cast<LocalHttpServiceAdvertisement>()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<LocalHttpServiceAdvertisement>> DiscoverMdnsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateUdpClient();
            var payload = BuildMdnsQuery("_http._tcp.local", "_https._tcp.local", "_home-assistant._tcp.local", "_hap._tcp.local");
            await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
            var responses = await ReceiveAsync(client, cancellationToken);
            return responses.SelectMany(response => ParseMdns(response.Buffer)).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<LocalHttpServiceAdvertisement>> DiscoverWsDiscoveryAsync(CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var request = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <e:Header>
                <w:MessageID>uuid:{{messageId}}</w:MessageID>
                <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
                <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
              </e:Header>
              <e:Body><d:Probe /></e:Body>
            </e:Envelope>
            """;

        try
        {
            using var client = CreateUdpClient();
            var payload = Encoding.UTF8.GetBytes(request);
            await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702));
            var responses = await ReceiveAsync(client, cancellationToken);
            return responses
                .SelectMany(response => ParseWsDiscovery(response.Buffer))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static UdpClient CreateUdpClient()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        return client;
    }

    private static async Task<IReadOnlyList<UdpReceiveResult>> ReceiveAsync(UdpClient client, CancellationToken cancellationToken)
    {
        var responses = new List<UdpReceiveResult>();
        var deadline = DateTimeOffset.UtcNow + DiscoveryTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(remaining);
            try
            {
                responses.Add(await client.ReceiveAsync(timeout.Token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return responses;
    }

    private static (string Location, string? DisplayName)? ParseHeader(byte[] payload, string locationHeader, string displayHeader)
    {
        var text = Encoding.UTF8.GetString(payload);
        string? location = null;
        string? displayName = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals(locationHeader, StringComparison.OrdinalIgnoreCase)) location = value;
            if (name.Equals(displayHeader, StringComparison.OrdinalIgnoreCase)) displayName = value;
        }

        return string.IsNullOrWhiteSpace(location) ? null : (location, displayName);
    }

    private static LocalHttpServiceAdvertisement? BuildFromUri(string value, string scope, string? displayName)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https" ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var port = uri.IsDefaultPort ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80 : uri.Port;
        var host = uri.Host.Trim('[', ']');
        var address = IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : null;
        return new LocalHttpServiceAdvertisement(host, address, port, scope, displayName);
    }

    private static byte[] BuildMdnsQuery(params string[] names)
    {
        var bytes = new List<byte>(512);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, names.Length);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);
        foreach (var name in names)
        {
            foreach (var label in name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var labelBytes = Encoding.ASCII.GetBytes(label);
                bytes.Add((byte)Math.Min(labelBytes.Length, 63));
                bytes.AddRange(labelBytes.Take(63));
            }

            bytes.Add(0);
            WriteUInt16(bytes, 12);
            WriteUInt16(bytes, 0x8001);
        }

        return bytes.ToArray();
    }

    private static IEnumerable<LocalHttpServiceAdvertisement> ParseMdns(byte[] payload)
    {
        if (payload.Length < 12) yield break;
        var offset = 4;
        var questions = ReadUInt16(payload, ref offset);
        var answers = ReadUInt16(payload, ref offset);
        var authorities = ReadUInt16(payload, ref offset);
        var additionals = ReadUInt16(payload, ref offset);
        for (var index = 0; index < questions && offset < payload.Length; index++)
        {
            _ = ReadDnsName(payload, ref offset);
            offset += 4;
        }

        var services = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var recordCount = answers + authorities + additionals;
        for (var index = 0; index < recordCount && offset < payload.Length; index++)
        {
            var name = ReadDnsName(payload, ref offset);
            if (string.IsNullOrWhiteSpace(name) || offset + 10 > payload.Length) yield break;
            var type = ReadUInt16(payload, ref offset);
            _ = ReadUInt16(payload, ref offset);
            offset += 4;
            var length = ReadUInt16(payload, ref offset);
            var start = offset;
            var end = Math.Min(payload.Length, start + length);
            if (type == 1 && length == 4)
            {
                var address = new IPAddress(payload.AsSpan(start, 4));
                if (IsPrivateIPv4(address)) addresses[name.TrimEnd('.')] = address;
            }
            else if (type == 33 && length >= 6)
            {
                var srvOffset = start + 4;
                var port = ReadUInt16(payload, ref srvOffset);
                var target = ReadDnsName(payload, ref srvOffset).TrimEnd('.');
                if (port is > 0 and <= 65535 && target.Length > 0) services[name.TrimEnd('.')] = (target, port);
            }

            offset = end;
        }

        foreach (var service in services)
        {
            var target = service.Value.Target;
            var address = addresses.GetValueOrDefault(target);
            var host = address?.ToString() ?? target;
            yield return new LocalHttpServiceAdvertisement(host, address, service.Value.Port, "mDNS", CleanServiceName(service.Key));
        }
    }

    private static IEnumerable<LocalHttpServiceAdvertisement> ParseWsDiscovery(byte[] payload)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(payload), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch
        {
            yield break;
        }

        foreach (var element in document.Descendants().Where(element => element.Name.LocalName.Equals("XAddrs", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var url in element.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var advertisement = BuildFromUri(url, "WS-Discovery", "WS-Discovery device");
                if (advertisement is not null) yield return advertisement;
            }
        }
    }

    private static string CleanServiceName(string value)
    {
        var name = value.TrimEnd('.');
        foreach (var suffix in new[] { "._http._tcp.local", "._https._tcp.local", "._home-assistant._tcp.local", "._hap._tcp.local" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length].Replace('\\', ' ');
        }

        return string.IsNullOrWhiteSpace(name) ? "mDNS service" : name.Replace('\\', ' ');
    }

    private static string ReadDnsName(byte[] payload, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var guard = 0;
        while (position < payload.Length && guard++ < 64)
        {
            var length = payload[position++];
            if (length == 0)
            {
                if (!jumped) offset = position;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position >= payload.Length) break;
                var pointer = ((length & 0x3F) << 8) | payload[position++];
                if (!jumped) offset = position;
                position = pointer;
                jumped = true;
                continue;
            }

            if (position + length > payload.Length) break;
            labels.Add(Encoding.UTF8.GetString(payload, position, length));
            position += length;
        }

        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(byte[] payload, ref int offset)
    {
        if (offset + 2 > payload.Length) { offset = payload.Length; return 0; }
        var value = (ushort)((payload[offset] << 8) | payload[offset + 1]);
        offset += 2;
        return value;
    }

    private static void WriteUInt16(ICollection<byte> bytes, int value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
               (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
}
