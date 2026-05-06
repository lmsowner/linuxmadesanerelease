namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareToolingStatusViewModel(
    bool HasAllRequiredPackages,
    bool CanScanNetwork,
    bool CanBrowseRemoteShares,
    bool CanCreateRemoteMounts,
    bool IsLocalHostRegistered,
    string? LocalHostName,
    string StatusMessage,
    string InstallCommand,
    IReadOnlyList<string> MissingPackageNames,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ShareToolingPackageViewModel> Packages);

public sealed record ShareToolingPackageViewModel(
    string PackageName,
    string Purpose,
    IReadOnlyList<string> Commands,
    bool IsInstalled,
    string Version);
