// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record RunbookParameterDefinition(
    string Name,
    string Label,
    RunbookParameterKind Kind,
    string Placeholder,
    string HelpText,
    bool IsRequired = true);
