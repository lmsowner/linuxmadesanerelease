// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum EdgeGatewayAuthMode
{
    PassThrough = 0,
    RequireLogin = 1,
    RequireMfa = 2,
    RequirePasskey = 3,
    Blocked = 4,
    TemporaryIpApproval = 5
}
