// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum EdgeGatewayAuthMode
{
    PassThrough = 0,
    RequireLogin = 1,
    RequireMfa = 2,
    RequirePasskey = 3,
    Blocked = 4
}
