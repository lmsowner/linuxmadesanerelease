// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Components.Ui;

public sealed record FileBrowserContextMenuRequest(
    double ClientX,
    double ClientY,
    string TargetPath,
    string ActionDirectoryPath,
    string DisplayName,
    bool IsDirectory,
    SftpItem? Item);
