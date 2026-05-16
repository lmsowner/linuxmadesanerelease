// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum AuthenticationType
{
    Password = 0,
    PrivateKey = 1,
    Agent = 2,
    PasswordAndPrivateKey = 3,
    Conditional = 4
}
