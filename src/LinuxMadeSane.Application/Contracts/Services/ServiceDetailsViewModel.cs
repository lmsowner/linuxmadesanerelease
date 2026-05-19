// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceDetailsViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceInspectionResult Inspection);
