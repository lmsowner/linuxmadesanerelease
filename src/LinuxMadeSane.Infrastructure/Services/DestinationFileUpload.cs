// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class DestinationFileUpload(
    Stream content,
    Func<CancellationToken, Task> commit,
    Func<ValueTask> cleanup) : IManagedHostFileUpload
{
    public Stream Content { get; } = content;
    public Task CompleteAsync(CancellationToken cancellationToken = default) => commit(cancellationToken);
    public ValueTask DisposeAsync() => cleanup();

    internal static IManagedHostFileUpload OpenLocal(string destination, long totalBytes)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        if (Path.Exists(destination)) throw new IOException("The destination already exists. Choose another name or remove it first.");
        var partial = Path.Combine(directory, $".lms-upload-{Guid.NewGuid():N}.partial");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 128 * 1024, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(partial, options);
        var committed = false;
        return new DestinationFileUpload(stream, async token =>
        {
            if (stream.Position != totalBytes) throw new IOException("The upload is incomplete.");
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
            await stream.DisposeAsync();
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
            committed = true;
        }, async () =>
        {
            await stream.DisposeAsync();
            if (!committed) File.Delete(partial);
        });
    }
}
