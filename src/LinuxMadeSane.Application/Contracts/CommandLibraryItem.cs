using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public sealed record CommandLibraryItem(
    Guid Id,
    Guid HostId,
    string HostName,
    IReadOnlyList<Guid> HostIds,
    IReadOnlyList<string> HostNames,
    string Name,
    string CommandText,
    string Description,
    bool RequiresSudo,
    bool IsQuickAccess,
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

    public bool IsLinked =>
        HostIds.Count > 1;
}
