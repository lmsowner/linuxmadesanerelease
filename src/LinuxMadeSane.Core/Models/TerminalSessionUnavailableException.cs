// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
