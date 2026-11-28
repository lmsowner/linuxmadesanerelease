// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Messaging;

namespace LinuxMadeSane.Core.Abstractions;

public interface IMessagingEmailSettingsStore
{
    Task<MessagingEmailSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MessagingEmailSettings settings, CancellationToken cancellationToken = default);
}
