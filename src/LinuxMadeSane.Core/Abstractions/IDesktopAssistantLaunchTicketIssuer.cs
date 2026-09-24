// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDesktopAssistantLaunchTicketIssuer
{
    DesktopAssistantLaunchTicket Issue(string returnUrl);
}
