// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TrustedNetworkEntry(
    Guid Id,
    string Label,
    string AddressOrCidr,
    string Description,
    bool IsEnabled,
    bool IsTrustedAccessEnabled,
    bool IsAuthenticationEnabled,
    bool IsBuiltIn,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
