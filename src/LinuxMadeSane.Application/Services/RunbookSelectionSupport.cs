// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using LinuxMadeSane.Core.Enums;
namespace LinuxMadeSane.Application.Services;
public static class RunbookSelectionSupport
{
    public static bool Matches(RunbookTargetKind kind, int count, bool isFolder) => kind switch
    {
        RunbookTargetKind.File => !isFolder && count == 1,
        RunbookTargetKind.Files => !isFolder && count > 0,
        RunbookTargetKind.Folder => isFolder && count == 1,
        _ => false
    };
    public static string Bind(string script, RunbookTargetKind kind, IReadOnlyList<string> paths, bool isFolder)
    {
        if (!Matches(kind, paths.Count, isFolder)) throw new InvalidOperationException("Select the files or folder expected by this runbook.");
        if (paths.Count > 1000 || paths.Any(p => !p.StartsWith('/') || p.Contains('\0')))
            throw new InvalidOperationException("Select up to 1,000 files with absolute paths on this host.");
        var arguments = string.Join(" ", paths.Select(p => "'" + p.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'"));
        return "#!/usr/bin/env bash\nset -- " + arguments + "\n" + RunbookExecutionCommandBuilder.NormalizeStoredScript(script);
    }
}
