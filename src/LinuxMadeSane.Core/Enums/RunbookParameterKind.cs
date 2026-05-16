// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum RunbookParameterKind
{
    Text = 0,
    RawText = 1,
    FilePath = 2,
    FolderPath = 3,
    ServiceName = 4,
    PackageName = 5,
    UserName = 6,
    GroupName = 7,
    HostName = 8,
    PortNumber = 9,
    Url = 10,
    SecretText = 11
}
