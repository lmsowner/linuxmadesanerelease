using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserProvisioningViewModel(
    Guid UserId,
    string Email,
    string LinuxUsername,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    string ManualEntryKey,
    string OtpUri);
