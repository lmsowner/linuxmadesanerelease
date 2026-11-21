// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISecurityAuthenticationService
{
    Task<SecurityAuthenticationResult> ValidateOtpAsync(string email, string otpCode, CancellationToken cancellationToken = default);
    Task<SecurityAuthenticationResult> ValidateDomainOtpAsync(string email, string otpCode, string hostname, CancellationToken cancellationToken = default);
}
