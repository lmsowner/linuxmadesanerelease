// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record TrustedNetworkEntryViewModel(
    Guid Id,
    string Label,
    string AddressOrCidr,
    string Description,
    bool IsEnabled,
    bool IsTrustedAccessEnabled,
    bool IsAuthenticationEnabled,
    bool IsBuiltIn);
