// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Models.MailRelay;
namespace LinuxMadeSane.Application.Interfaces;
public sealed record MailRelayOutboundInspection(MailRelayPreflightResult Preflight, bool UseUpstream, string Summary)
{ public int DestinationPort { get; init; } = 25; }
public interface IMailRelayOutboundService
{
    Task<MailRelayOutboundInspection> InspectAsync(MailRelayOutboundSettings settings, MailRelayPreflightResult preflight, CancellationToken cancellationToken = default);
}
