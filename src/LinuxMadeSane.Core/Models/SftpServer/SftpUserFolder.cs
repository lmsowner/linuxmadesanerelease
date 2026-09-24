// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpUserFolder(
    string BasePath,
    string ChrootPath,
    string WritablePath,
    string ChrootOwner,
    string ChrootGroup,
    string ChrootMode,
    string WritableOwner,
    string WritableGroup,
    string WritableMode);
