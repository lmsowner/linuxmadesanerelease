namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecuritySettingsPageViewModel(
    IReadOnlyList<TrustedNetworkEntryViewModel> TrustedNetworks,
    IReadOnlyList<SecurityUserViewModel> Users,
    SecurityMessagingSettingsViewModel Messaging);
