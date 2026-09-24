// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpServerInspectionService
{
    Task<SftpHostConfiguration> InspectAsync(CancellationToken cancellationToken = default);
}
