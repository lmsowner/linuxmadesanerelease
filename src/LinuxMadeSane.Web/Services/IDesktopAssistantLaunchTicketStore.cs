// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web.Services;

public interface IDesktopAssistantLaunchTicketStore : IDesktopAssistantLaunchTicketIssuer
{
    bool TryConsume(string? token, out string returnUrl);

    bool TryValidate(string? token);
}
