// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum AuthenticationType
{
    Password = 0,
    PrivateKey = 1,
    Agent = 2,
    PasswordAndPrivateKey = 3,
    Conditional = 4
}
