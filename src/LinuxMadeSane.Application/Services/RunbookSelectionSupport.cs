// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;
namespace LinuxMadeSane.Application.Services;
public static class RunbookSelectionSupport
{
    public static string Label(RunbookTargetKind kind) => kind switch
    {
        RunbookTargetKind.File => "One file", RunbookTargetKind.Files => "Multiple files",
        RunbookTargetKind.Folder => "One folder", RunbookTargetKind.Folders => "Multiple folders",
        RunbookTargetKind.FilesAndFolders => "Files and folders", _ => "Machine (no paths)"
    };

    public static bool Matches(RunbookTargetKind kind, int count, bool isFolder) =>
        Matches(kind, isFolder ? 0 : count, isFolder ? count : 0);

    public static bool Matches(RunbookTargetKind kind, int fileCount, int folderCount) => kind switch
    {
        RunbookTargetKind.File => fileCount == 1 && folderCount == 0,
        RunbookTargetKind.Files => fileCount > 0 && folderCount == 0,
        RunbookTargetKind.Folder => folderCount == 1 && fileCount == 0,
        RunbookTargetKind.Folders => folderCount > 0 && fileCount == 0,
        RunbookTargetKind.FilesAndFolders => fileCount + folderCount > 0,
        _ => false
    };

    public static IReadOnlyList<string> ParsePaths(string text) =>
        (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();

    public static string Bind(string script, RunbookTargetKind kind, IReadOnlyList<string> paths, bool isFolder)
    {
        if (!Matches(kind, paths.Count, isFolder)) throw new InvalidOperationException("Select the files or folders expected by this runbook.");
        return BindPaths(script, kind, paths);
    }

    public static string BindPaths(string script, RunbookTargetKind kind, IReadOnlyList<string> paths)
    {
        if (!Enum.IsDefined(kind)) throw new InvalidOperationException("Choose a valid runbook input type.");
        if (kind == RunbookTargetKind.Machine ? paths.Count != 0 : paths.Count == 0)
            throw new InvalidOperationException(kind == RunbookTargetKind.Machine ? "This runbook takes no paths." : $"Enter paths for: {Label(kind)}.");
        if ((kind == RunbookTargetKind.File || kind == RunbookTargetKind.Folder) && paths.Count != 1)
            throw new InvalidOperationException($"This runbook expects {Label(kind).ToLowerInvariant()}.");
        if (paths.Count > 1000 || paths.Any(p => !p.StartsWith('/') || p.Contains('\0')))
            throw new InvalidOperationException("Enter up to 1,000 absolute paths on the execution host.");
        var arguments = string.Join(" ", paths.Select(p => "'" + p.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'"));
        return "#!/usr/bin/env bash\nset -- " + arguments + "\n" +
            "lms_input_error() { printf 'Runbook input: %s\\n' \"$*\" >&2; exit 1; }\n" +
            RunbookScaffold.BuildInputValidation(kind, "lms_input_error") + "\n" +
            RunbookExecutionCommandBuilder.NormalizeStoredScript(script);
    }
}
