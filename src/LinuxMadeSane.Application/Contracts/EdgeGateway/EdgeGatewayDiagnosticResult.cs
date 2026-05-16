// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayDiagnosticResult(
    EdgeGatewayDiagnosticStatus Status,
    string Summary,
    IReadOnlyList<string> Checks);
