// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
