// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalHttpServiceProxyCompatibilityService
{
    Task<LocalHttpServiceProxyProfile> TestAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        OnDemandAppProxyPreferences preferences,
        CancellationToken cancellationToken = default) =>
        SelectAsync(endpoint, publicHostname, preferences, cancellationToken);

    Task<LocalHttpServiceProxyProfile> SelectAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        OnDemandAppProxyPreferences preferences,
        CancellationToken cancellationToken = default);
}
