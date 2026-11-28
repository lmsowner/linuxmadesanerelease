// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LinuxShareUserEntity
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PrimaryGroup { get; set; } = string.Empty;

    public string SupplementaryGroupsJson { get; set; } = "[]";

    public string HomeDirectory { get; set; } = string.Empty;

    public string LoginShell { get; set; } = "/bin/bash";

    public bool IsEnabled { get; set; }
}
