// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityMessagingTestResult(
    bool Succeeded,
    bool Attempted,
    string Message);
