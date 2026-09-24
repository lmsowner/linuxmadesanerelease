// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Services;

internal static class StreamReadSupport
{
    public static int ReadUpTo(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken = default)
    {
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = stream.Read(buffer, totalBytesRead, buffer.Length - totalBytesRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }

    public static async ValueTask<int> ReadUpToAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken = default)
    {
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalBytesRead, buffer.Length - totalBytesRead),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }
}
