// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Contracts.MailRelay;

public sealed record MailRelayClientSaveRequest(
    Guid? ClientId,
    string Name,
    string Username,
    string Password,
    IReadOnlyList<Guid> AllowedDomainIds);

public sealed record MailRelayClientSaveResult(
    bool Success,
    MailRelayClient? Client,
    bool PasswordChanged,
    string Summary);
