// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceControlResult(
    Guid ServiceId,
    string UnitName,
    ServiceControlAction Action,
    bool Success,
    string Message);
