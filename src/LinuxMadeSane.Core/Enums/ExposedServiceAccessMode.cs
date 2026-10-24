// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ExposedServiceAccessMode
{
    OnlyMe = 0,
    EmailAllowList = 1,
    EmailDomainAllowList = 2,
    NoAccessProtection = 3
}
