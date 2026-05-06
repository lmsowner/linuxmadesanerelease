using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record RunbookParameterDefinition(
    string Name,
    string Label,
    RunbookParameterKind Kind,
    string Placeholder,
    string HelpText,
    bool IsRequired = true);
