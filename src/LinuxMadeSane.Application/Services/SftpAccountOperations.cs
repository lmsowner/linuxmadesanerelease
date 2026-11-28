// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Application.Services;

// Keep ownership checks and file operations ordered with changes made in the SFTP manager.
public static class SftpAccountOperations
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private static readonly AsyncLocal<string?> Current = new();

    public static async Task<T> RunAsync<T>(string name, Func<Task<T>> operation, CancellationToken token)
    {
        var key = name.Trim().ToLowerInvariant();
        if (Current.Value == key) return await operation();
        var gate = Gates[(uint)StringComparer.Ordinal.GetHashCode(key) % Gates.Length];
        await gate.WaitAsync(token);
        var previous = Current.Value;
        try
        {
            Current.Value = key;
            return await operation();
        }
        finally
        {
            Current.Value = previous;
            gate.Release();
        }
    }
}
