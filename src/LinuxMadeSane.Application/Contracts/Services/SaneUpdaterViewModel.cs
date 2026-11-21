// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record SaneUpdaterViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceUpdatePlan UpdatePlan);
