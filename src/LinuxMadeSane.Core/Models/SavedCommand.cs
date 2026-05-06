using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Models;

public sealed record SavedCommand(
    Guid Id,
    Guid HostId,
    string Name,
    string CommandText,
    string Description,
    bool RequiresSudo,
    bool IsQuickAccess = false,
    bool IsTemplate = false,
    Guid? TemplateSourceId = null,
    Guid? LinkGroupId = null,
    IReadOnlyList<RunbookParameterDefinition>? ParameterDefinitions = null,
    IReadOnlyDictionary<string, string>? ParameterValueSnapshot = null)
{
    public bool IsScript =>
        CommandText.Contains('\n') || CommandText.Contains('\r');

    public bool HasParameters =>
        ParameterDefinitions?.Count > 0;
}
