// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Messaging;

namespace LinuxMadeSane.Core.Abstractions;

public interface IMessagingEmailSettingsStore
{
    Task<MessagingEmailSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MessagingEmailSettings settings, CancellationToken cancellationToken = default);
}
