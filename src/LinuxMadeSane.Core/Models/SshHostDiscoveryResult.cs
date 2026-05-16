// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record SshHostDiscoveryResult(
    IReadOnlyList<DiscoveredSshHost> Hosts,
    string StatusMessage,
    IReadOnlyList<string> Notes);
