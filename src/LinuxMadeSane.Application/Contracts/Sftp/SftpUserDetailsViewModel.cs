// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed record SftpUserDetailsViewModel(
    SftpManagedUser User,
    IReadOnlyList<SftpAuditEntry> AuditEntries);
