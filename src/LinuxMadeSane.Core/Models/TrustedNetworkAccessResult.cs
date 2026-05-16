// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TrustedNetworkAccessResult(
    string RemoteAddress,
    string RequestHost,
    bool IsTrusted,
    string? MatchedRuleLabel,
    bool IsLocalRequestTarget,
    bool RequiresAuthentication,
    bool IsAllowed,
    bool IsTrustedAccessEnabled,
    bool IsAuthenticationEnabled);
