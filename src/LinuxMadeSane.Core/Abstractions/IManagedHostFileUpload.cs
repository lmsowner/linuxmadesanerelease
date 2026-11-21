// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

// A bounded streaming write to a private partial file on the destination filesystem.
// Disposing without completing removes the partial file and preserves the destination.
public interface IManagedHostFileUpload : IAsyncDisposable
{
    Stream Content { get; }
    Task CompleteAsync(CancellationToken cancellationToken = default);
}
