// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web.Services;

public interface IDesktopAssistantLaunchTicketStore : IDesktopAssistantLaunchTicketIssuer
{
    bool TryConsume(string? token, out string returnUrl);
}
