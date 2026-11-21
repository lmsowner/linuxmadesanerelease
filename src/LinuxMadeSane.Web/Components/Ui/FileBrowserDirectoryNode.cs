// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Web.Components.Ui;

public sealed record FileBrowserDirectoryNode(
    string Name,
    string Path,
    bool IsExpanded,
    bool IsCurrent,
    bool IsAncestor,
    bool IsBlocked,
    bool CanExpand,
    IReadOnlyList<FileBrowserDirectoryNode> Children);
