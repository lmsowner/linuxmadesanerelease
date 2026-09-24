// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpChrootService
{
    SftpUserFolder BuildFolderLayout(string basePath, string userName);

    void ValidateFolderLayout(SftpUserFolder folder);
}
