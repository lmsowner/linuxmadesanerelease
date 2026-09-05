// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed class TerminalSessionUnavailableException : InvalidOperationException
{
    public TerminalSessionUnavailableException(
        string message,
        bool canRetryCommand,
        bool connectionRecovered,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CanRetryCommand = canRetryCommand;
        ConnectionRecovered = connectionRecovered;
    }

    public bool CanRetryCommand { get; }

    public bool ConnectionRecovered { get; }
}
