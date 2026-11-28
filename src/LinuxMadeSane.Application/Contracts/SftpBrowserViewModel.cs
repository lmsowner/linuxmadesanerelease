// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public sealed record SftpBrowserViewModel(
    ManagedHost Host,
    string CurrentPath,
    IReadOnlyList<SftpItem> Items);
