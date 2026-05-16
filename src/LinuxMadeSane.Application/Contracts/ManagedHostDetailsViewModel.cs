// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public sealed record ManagedHostDetailsViewModel(
    ManagedHost Host,
    HostConnectionTestResult ConnectionTest,
    ServerHealthSnapshot HealthSnapshot,
    IReadOnlyList<SavedCommand> SavedCommands,
    IReadOnlyList<SshCredentialType> CredentialTypes,
    ManagedHostCapabilityProfile Capabilities);
