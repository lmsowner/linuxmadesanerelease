// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum RemoteAccessSshAuthenticationMode
{
    Password = 0,
    PasswordAndKey = 1,
    KeyOnly = 2,
    PasswordOrKey = 3
}
