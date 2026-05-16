// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISecurityAuthenticationService
{
    Task<SecurityAuthenticationResult> ValidateOtpAsync(string email, string otpCode, CancellationToken cancellationToken = default);
}
