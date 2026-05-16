// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record CurrentSystemMount(
    string SourcePath,
    string LocalMountPath,
    string FileSystemType,
    string MountOptions,
    bool IsReadOnly,
    bool IsNetworkMount,
    bool IsManagedByLms);
