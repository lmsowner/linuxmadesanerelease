using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISecurityAuthenticationService
{
    Task<SecurityAuthenticationResult> ValidateOtpAsync(string identifier, string otpCode, CancellationToken cancellationToken = default);
}
