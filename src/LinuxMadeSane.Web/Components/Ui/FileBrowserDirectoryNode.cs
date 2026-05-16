// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
