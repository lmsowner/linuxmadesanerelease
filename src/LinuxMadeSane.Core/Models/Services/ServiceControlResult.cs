// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceControlResult(
    Guid ServiceId,
    string UnitName,
    ServiceControlAction Action,
    bool Success,
    string Message);
