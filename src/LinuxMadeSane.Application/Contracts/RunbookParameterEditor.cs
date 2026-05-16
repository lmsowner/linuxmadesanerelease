// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts;

public sealed class RunbookParameterEditor
{
    public string Name { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public RunbookParameterKind Kind { get; set; } = RunbookParameterKind.Text;

    public string Placeholder { get; set; } = string.Empty;

    public string HelpText { get; set; } = string.Empty;

    public bool IsRequired { get; set; } = true;

    public string Value { get; set; } = string.Empty;
}
