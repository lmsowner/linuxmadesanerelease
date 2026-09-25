// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareFolderBrowseResult(
    string Target,
    string ShareName,
    string CurrentPath,
    bool UsedAuthentication,
    string StatusMessage,
    IReadOnlyList<RemoteSambaFolder> Folders,
    IReadOnlyList<string> Notes);
