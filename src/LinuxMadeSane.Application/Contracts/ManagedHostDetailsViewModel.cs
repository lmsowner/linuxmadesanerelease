using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts;

public sealed record ManagedHostDetailsViewModel(
    ManagedHost Host,
    HostConnectionTestResult ConnectionTest,
    ServerHealthSnapshot HealthSnapshot,
    IReadOnlyList<SavedCommand> SavedCommands,
    IReadOnlyList<SshCredentialType> CredentialTypes,
    ManagedHostCapabilityProfile Capabilities);
