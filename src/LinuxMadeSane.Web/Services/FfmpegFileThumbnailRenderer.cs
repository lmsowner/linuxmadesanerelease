// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;

namespace LinuxMadeSane.Web.Services;

public interface IFileThumbnailRenderer
{
    Task<bool> RenderJpegAsync(
        string sourcePath,
        string destinationPath,
        int maxDimension,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class FfmpegFileThumbnailRenderer(ILogger<FfmpegFileThumbnailRenderer> logger) : IFileThumbnailRenderer
{
    public async Task<bool> RenderJpegAsync(
        string sourcePath,
        string destinationPath,
        int maxDimension,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale={maxDimension}:{maxDimension}:force_original_aspect_ratio=decrease:force_divisible_by=2");
        startInfo.ArgumentList.Add("-threads");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add("4");
        startInfo.ArgumentList.Add(destinationPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }

            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                logger.LogDebug("Thumbnail rendering timed out for {SourcePath}.", sourcePath);
                return false;
            }

            var error = await errorTask;
            if (process.ExitCode == 0 && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                return true;
            }

            logger.LogDebug(
                "Thumbnail rendering failed for {SourcePath} with exit code {ExitCode}: {Error}",
                sourcePath,
                process.ExitCode,
                error.Trim());
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            TryKill(process);
            logger.LogDebug(exception, "Thumbnail rendering is unavailable for {SourcePath}.", sourcePath);
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the state check and the kill request.
        }
    }
}
