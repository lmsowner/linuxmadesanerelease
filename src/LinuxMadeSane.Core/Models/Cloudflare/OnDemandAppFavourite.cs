// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
