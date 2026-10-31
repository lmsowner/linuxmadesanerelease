// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class PrivilegedFileBrowsingService
{
    private static readonly SemaphoreSlim DuplicateGate = new(2);
    private static readonly Lazy<string> DuplicateScript = new(() =>
    {
        using var stream = typeof(PrivilegedFileBrowsingService).Assembly.GetManifestResourceStream(
            "LinuxMadeSane.Infrastructure.Services.FileSearch.folder-duplicates.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public async Task<FolderDuplicateResult> ProcessFolderDuplicatesAsync(ManagedHost host, string path,
        ManagedHostConnectionProfile profile, FolderDuplicateResult? scan, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
            throw new InvalidOperationException("Choose an absolute folder path.");
        var input = JsonSerializer.SerializeToUtf8Bytes(new { RootPath = path, Scan = scan });
        if (input.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Too many duplicates for one operation. Use a smaller folder.");
        await DuplicateGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            PrivilegedCommandResult result;
            if (AiLocalMachine.IsLocalMachine(host.Id) && !profile.UseSshTransport)
            {
                var local = await commandRunner.RunAsync(new LinuxCommandRequest("/usr/bin/python3", ["-c", DuplicateScript.Value],
                    profile.UseSudo, CommandTimeout, "Check file hashes and move matching copies within their folder.", host.DefaultWorkingDirectory)
                    { StandardInputBytes = input }, false, timeout.Token);
                result = new(local.ExitCode, local.StandardOutput, local.StandardError);
            }
            else
                result = await ExecuteRemoteAsync(host, profile, "/usr/bin/python3", ["-c", DuplicateScript.Value], input, timeout.Token, profile.UseSudo);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "The folder check could not complete. No files were deleted. Refresh the folder before retrying."
                    : result.StandardError.Trim());
            return JsonSerializer.Deserialize<FolderDuplicateResult>(result.StandardOutput, JsonOptions)
                ?? throw new InvalidOperationException("The host did not return a folder-check result.");
        }
        finally { DuplicateGate.Release(); }
    }
}
