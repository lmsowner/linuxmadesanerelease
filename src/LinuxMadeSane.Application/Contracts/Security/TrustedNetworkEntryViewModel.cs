namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record TrustedNetworkEntryViewModel(
    Guid Id,
    string Label,
    string AddressOrCidr,
    string Description,
    bool IsEnabled,
    bool IsTrustedAccessEnabled,
    bool IsAuthenticationEnabled,
    bool IsBuiltIn);
