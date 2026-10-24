// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models;
namespace LinuxMadeSane.Web.Components.Ui;
public enum FileSearchBulkAction { CopyClipboard, CutClipboard, CopyTo, MoveTo, Delete, Archive }
public sealed record FileSearchBulkActionRequest(FileSearchBulkAction Action, IReadOnlyList<FileSearchMatch> Items,
    string? Destination = null, ArchiveFormat ArchiveFormat = ArchiveFormat.Zip, bool CreateDestinationFolder = false);
