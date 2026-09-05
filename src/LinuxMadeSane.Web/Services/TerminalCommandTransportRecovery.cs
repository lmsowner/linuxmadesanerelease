// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public static class TerminalCommandTransportRecovery
{
    public static async Task SendWithReconnectAsync(
        Func<Task> sendAsync,
        Func<Task<bool>> reconnectAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(reconnectAsync);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sendAsync();
            return;
        }
        catch (TerminalSessionUnavailableException exception)
        {
            var reconnected = await reconnectAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!reconnected)
            {
                throw new TerminalSessionUnavailableException(
                    "The terminal connection was lost and LMS could not reconnect automatically. Reconnect the terminal before continuing.",
                    canRetryCommand: false,
                    connectionRecovered: false,
                    exception);
            }

            if (!exception.CanRetryCommand)
            {
                throw new TerminalSessionUnavailableException(
                    "LMS reconnected the terminal, but it could not confirm whether the interrupted command was delivered. The AI must verify the current state before making another change.",
                    canRetryCommand: false,
                    connectionRecovered: true,
                    exception);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sendAsync();
        }
        catch (TerminalSessionUnavailableException exception)
        {
            throw new TerminalSessionUnavailableException(
                "LMS reconnected the terminal, but the replacement connection failed while resending the command.",
                canRetryCommand: false,
                connectionRecovered: false,
                exception);
        }
    }

}
