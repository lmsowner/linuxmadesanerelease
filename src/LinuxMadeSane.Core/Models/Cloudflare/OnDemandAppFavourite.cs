// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public enum OnDemandAppTargetAddressPreference
{
    DiscoveredIp,
    DiscoveredHostname
}

public enum OnDemandAppSchemePreference
{
    Automatic,
    Http,
    Https
}

public enum OnDemandAppHostHeaderPreference
{
    Automatic,
    Upstream,
    Public
}

public enum OnDemandAppForwardedForPreference
{
    Automatic,
    Strip,
    Preserve
}

public sealed record OnDemandAppProxyPreferences(
    OnDemandAppTargetAddressPreference TargetAddress = OnDemandAppTargetAddressPreference.DiscoveredIp,
    OnDemandAppSchemePreference Scheme = OnDemandAppSchemePreference.Automatic,
    OnDemandAppHostHeaderPreference HostHeader = OnDemandAppHostHeaderPreference.Automatic,
    OnDemandAppForwardedForPreference ForwardedFor = OnDemandAppForwardedForPreference.Automatic,
    bool ConnectAsLms = false,
    string SourceInterface = "",
    string SourceAddress = "");

public sealed record OnDemandAppFavourite(
    string ServiceKey,
    LocalHttpServiceEndpoint? Endpoint,
    OnDemandAppProxyPreferences ProxyPreferences,
    DateTimeOffset UpdatedAtUtc);
