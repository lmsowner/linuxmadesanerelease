// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDesktopAssistantLaunchTicketIssuer
{
    DesktopAssistantLaunchTicket Issue(string returnUrl);
}
