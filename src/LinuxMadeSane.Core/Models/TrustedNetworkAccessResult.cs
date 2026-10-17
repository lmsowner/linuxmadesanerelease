// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

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
    bool IsAuthenticationEnabled,
    NetworkAccessDeniedResponseMode DeniedResponseMode = NetworkAccessDeniedResponseMode.AccessDeniedPage);
