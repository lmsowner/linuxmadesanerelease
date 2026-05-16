// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ExposedServiceAccessMode
{
    OnlyMe = 0,
    EmailAllowList = 1,
    EmailDomainAllowList = 2,
    NoAccessProtection = 3
}
