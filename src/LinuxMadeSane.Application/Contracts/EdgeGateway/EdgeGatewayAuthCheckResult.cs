// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayAuthCheckResult(
    int StatusCode,
    EdgeGatewayDecision Decision,
    string Reason,
    string? RedirectLocation = null,
    string? UserName = null,
    string? UserEmail = null,
    string? Groups = null);
