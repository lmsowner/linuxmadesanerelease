// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayClientService
{
    string GeneratePassword();

    Task<MailRelayClientSaveResult> SaveAsync(
        MailRelayClientSaveRequest request,
        CancellationToken cancellationToken = default);
}
