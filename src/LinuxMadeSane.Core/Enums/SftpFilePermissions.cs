// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Enums;

[Flags]
public enum SftpFilePermissions
{
    None = 0,
    Download = 1,
    Upload = 2,
    CreateFiles = 4,
    DeleteFiles = 8,
    CreateFolders = 16,
    DeleteFolders = 32,
    Rename = 64,
    All = 127
}
