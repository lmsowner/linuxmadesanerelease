namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record SshfsToolingStatusViewModel(
    bool HasAllRequiredPackages,
    bool CanCreateSshfsMounts,
    bool IsLocalHostRegistered,
    string? LocalHostName,
    string StatusMessage,
    string InstallCommand,
    IReadOnlyList<string> MissingPackageNames,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ShareToolingPackageViewModel> Packages);
