// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record TrustedNetworkEntryViewModel(
    Guid Id,
    string Label,
    string AddressOrCidr,
    string Description,
    bool IsEnabled,
    bool IsTrustedAccessEnabled,
    bool IsAuthenticationEnabled,
    bool IsBuiltIn,
    NetworkAccessDeniedResponseMode DeniedResponseMode);
