// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SshfsMountHostCandidate(
    Guid HostId,
    string Name,
    string Hostname,
    int Port,
    string UserName,
    string DefaultRemotePath,
    AuthenticationType PrimaryAuthenticationType,
    AuthenticationType? FallbackAuthenticationType,
    bool HasStoredPrivateKey,
    bool HasPrivateKeyPassphrase,
    bool CanMountWithSshfs,
    string StatusMessage);
