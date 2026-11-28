// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed class FileAccessPathNotFoundException : InvalidOperationException
{
    public FileAccessPathNotFoundException(string requestedPath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        RequestedPath = requestedPath;
    }

    public string RequestedPath { get; }
}
