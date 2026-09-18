// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceProxyProfile(
    LocalHttpServiceEndpoint Endpoint,
    bool UsePublicHostHeader,
    bool StripForwardedFor,
    string SelectionReason);
