// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record RemoteShareFolderBrowseRequest(
    string Target,
    string ShareName,
    string RemotePath,
    string? UserName,
    string? Password,
    string? Domain);
