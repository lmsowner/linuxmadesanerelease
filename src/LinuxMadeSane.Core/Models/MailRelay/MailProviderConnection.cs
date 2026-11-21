// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Models.MailRelay;

// Saved connections are independent of the one installed live delivery route.
public sealed record MailProviderConnection(Guid Id, string Name, MailRelayOutboundSettings Settings,
    DateTimeOffset UpdatedUtc, DateTimeOffset? LastConnectionCheckUtc = null, DateTimeOffset? LastTestAcceptedUtc = null);
