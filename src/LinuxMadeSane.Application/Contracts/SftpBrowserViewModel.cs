// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public sealed record SftpBrowserViewModel(
    ManagedHost Host,
    string CurrentPath,
    IReadOnlyList<SftpItem> Items);
