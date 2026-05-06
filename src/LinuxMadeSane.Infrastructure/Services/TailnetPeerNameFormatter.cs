namespace LinuxMadeSane.Infrastructure.Services;

// Guardrail: tailnet peer display-name rules live here so SMB and SSH discovery
// do not drift into separate fallback naming logic.
internal static class TailnetPeerNameFormatter
{
    public static string? ResolveFriendlyName(
        string? customName,
        string? hostName,
        string? dnsName)
    {
        var preferredName = NormalizeFriendlyName(customName);
        if (string.IsNullOrWhiteSpace(preferredName))
        {
            preferredName = NormalizeFriendlyName(hostName);
        }

        if (string.IsNullOrWhiteSpace(preferredName))
        {
            preferredName = NormalizeFriendlyName(dnsName);
        }

        return preferredName;
    }

    public static string BuildDisplayName(
        string? customName,
        string? hostName,
        string? dnsName,
        string? ipAddress,
        string? ownerLabel,
        string fallbackLabel)
    {
        var preferredName = ResolveFriendlyName(customName, hostName, dnsName);

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            return preferredName;
        }

        var normalizedIpAddress = NullIfWhiteSpace(ipAddress);
        if (!string.IsNullOrWhiteSpace(ownerLabel))
        {
            return string.IsNullOrWhiteSpace(normalizedIpAddress)
                ? $"{ownerLabel} shared device"
                : $"{ownerLabel} shared device ({normalizedIpAddress})";
        }

        return normalizedIpAddress ?? fallbackLabel;
    }

    public static string BuildTarget(
        string? hostName,
        string? dnsName,
        string? ipAddress)
    {
        var normalizedDnsName = NormalizeDnsName(dnsName);
        if (!string.IsNullOrWhiteSpace(normalizedDnsName) &&
            !string.IsNullOrWhiteSpace(ResolveFriendlyName(null, null, normalizedDnsName)))
        {
            return normalizedDnsName;
        }

        var normalizedHostName = NormalizeDnsName(hostName);
        if (!string.IsNullOrWhiteSpace(normalizedHostName) &&
            !string.IsNullOrWhiteSpace(ResolveFriendlyName(null, normalizedHostName, null)))
        {
            return normalizedHostName;
        }

        var normalizedIpAddress = NullIfWhiteSpace(ipAddress);
        return normalizedIpAddress ?? "tailnet-peer";
    }

    public static string? NormalizeDnsName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? NormalizeFriendlyName(string? value)
    {
        var normalized = NormalizeDnsName(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var firstLabel = normalized.Contains('.', StringComparison.Ordinal)
            ? normalized.Split('.', StringSplitOptions.RemoveEmptyEntries)[0]
            : normalized;

        return IsGenericSharedPeerLabel(firstLabel)
            ? null
            : firstLabel;
    }

    private static bool IsGenericSharedPeerLabel(string value) =>
        value.Equals("device-of-shared-to-user", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("device-of-shared-to-", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
