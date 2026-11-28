// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Web.Services;

internal static class FileBrowserStartingDirectory
{
    internal static Task<string> ResolveAsync(ManagedHostKind kind, string? requestedPath, string currentPath, Func<Task<string>> readServerHome)
        => kind == ManagedHostKind.SftpHost && string.IsNullOrWhiteSpace(requestedPath)
            ? readServerHome()
            : Task.FromResult(currentPath);
}
