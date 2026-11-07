// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record RunbookParameterDefinition(
    string Name,
    string Label,
    RunbookParameterKind Kind,
    string Placeholder,
    string HelpText,
    bool IsRequired = true);
