using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISecurityAuthenticationService
{
    Task<SecurityAuthenticationResult> ValidateOtpAsync(string email, string otpCode, CancellationToken cancellationToken = default);
}
