// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
