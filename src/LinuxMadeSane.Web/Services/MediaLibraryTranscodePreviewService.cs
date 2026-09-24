// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MediaLibrary;

namespace LinuxMadeSane.Web.Services;

public sealed class MediaLibraryTranscodePreviewService(
    IMediaLibraryIntegrationDataService dataService,
    ILogger<MediaLibraryTranscodePreviewService> logger)
{
    private const double HlsVodSegmentSeconds = 4;
    private static readonly object FfmpegGate = new();
    private static readonly ConcurrentDictionary<Guid, HlsPreviewSession> HlsSessions = new();
    private static readonly SemaphoreSlim HlsSessionGate = new(1, 1);
    private static readonly Regex HlsMediaPartNamePattern = new(@"^(init\.mp4|segment-\d{5}\.(m4s|ts))$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HlsSegmentNamePattern = new(@"^segment-\d{5}\.m4s$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HlsVodSegmentNamePattern = new(@"^segment-(\d{5})\.ts$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static int activeFfmpegJobs;

    public async Task<MediaTranscodePreviewResult> OpenPreviewStreamAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var settings = await dataService.GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException("The Media Library Streaming Integration is disabled.");
        }

        if (settings.StreamingMode != MediaStreamingMode.TranscodeFallback || !settings.EnableTranscoding)
        {
            throw new InvalidOperationException("FFmpeg transcoding is not enabled for media previews.");
        }

        var ffmpegStatus = await dataService.GetFfmpegStatusAsync(cancellationToken);
        if (!ffmpegStatus.FfmpegAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available on this LMS host.");
        }

        var streamResult = await dataService.OpenStreamAsync(itemId, cancellationToken);
        await streamResult.ContentStream.DisposeAsync();

        if (streamResult.Item.MediaKind is not (MediaKind.Video or MediaKind.Audio))
        {
            throw new InvalidOperationException("Only audio and video media items can be converted for browser preview.");
        }

        var lease = await AcquireFfmpegLeaseAsync(settings.MaxConcurrentFfmpegJobs, cancellationToken);
        try
        {
            var (process, processState) = StartFfmpeg(ffmpegStatus.FfmpegPath, streamResult.Item);
            var contentType = streamResult.Item.MediaKind == MediaKind.Audio ? "audio/mp4" : "video/mp4";
            return new MediaTranscodePreviewResult(
                new FfmpegPreviewStream(process, process.StandardOutput.BaseStream, lease, logger, streamResult.Item.Id, processState),
                contentType);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<MediaTranscodePreviewResult> OpenWebmPreviewStreamAsync(
        Guid itemId,
        double? startSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await dataService.GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException("The Media Library Streaming Integration is disabled.");
        }

        if (settings.StreamingMode != MediaStreamingMode.TranscodeFallback || !settings.EnableTranscoding)
        {
            throw new InvalidOperationException("FFmpeg transcoding is not enabled for media previews.");
        }

        var ffmpegStatus = await dataService.GetFfmpegStatusAsync(cancellationToken);
        if (!ffmpegStatus.FfmpegAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available on this LMS host.");
        }

        var streamResult = await dataService.OpenStreamAsync(itemId, cancellationToken);
        await streamResult.ContentStream.DisposeAsync();

        if (streamResult.Item.MediaKind != MediaKind.Video)
        {
            throw new InvalidOperationException("Only video media items can use WebM browser preview.");
        }

        var lease = await AcquireFfmpegLeaseAsync(settings.MaxConcurrentFfmpegJobs, cancellationToken);
        try
        {
            var (process, processState) = StartWebmFfmpeg(ffmpegStatus.FfmpegPath, streamResult.Item, startSeconds);
            return new MediaTranscodePreviewResult(
                new FfmpegPreviewStream(process, process.StandardOutput.BaseStream, lease, logger, streamResult.Item.Id, processState),
                "video/webm");
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<MediaHlsPreviewPlaylistResult> OpenHlsPlaylistAsync(
        Guid itemId,
        string? requestToken,
        CancellationToken cancellationToken = default)
    {
        var (item, durationSeconds) = await GetVodHlsItemAsync(itemId, cancellationToken);
        var playlist = BuildVodHlsPlaylist(item, durationSeconds, requestToken);
        return new MediaHlsPreviewPlaylistResult(
            playlist,
            "application/vnd.apple.mpegurl");
    }

    public async Task<MediaHlsPreviewSegmentResult> OpenHlsSegmentAsync(
        Guid itemId,
        string segmentName,
        CancellationToken cancellationToken = default)
    {
        if (!HlsMediaPartNamePattern.IsMatch(segmentName))
        {
            throw new FileNotFoundException("Unknown media preview segment.");
        }

        var vodSegmentMatch = HlsVodSegmentNamePattern.Match(segmentName);
        if (vodSegmentMatch.Success)
        {
            return await OpenVodHlsSegmentAsync(
                itemId,
                int.Parse(vodSegmentMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                cancellationToken);
        }

        if (!HlsSessions.TryGetValue(itemId, out var session))
        {
            session = await EnsureHlsSessionAsync(itemId, cancellationToken);
        }

        session.Touch();
        var segmentPath = Path.Combine(session.DirectoryPath, segmentName);
        if (!File.Exists(segmentPath))
        {
            throw new FileNotFoundException("The media preview segment is not ready.");
        }

        var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 128,
            useAsync: true);
        return new MediaHlsPreviewSegmentResult(stream, "video/mp4");
    }

    private async Task<(MediaItem Item, double DurationSeconds)> GetVodHlsItemAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var settings = await dataService.GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException("The Media Library Streaming Integration is disabled.");
        }

        if (settings.StreamingMode != MediaStreamingMode.TranscodeFallback || !settings.EnableTranscoding)
        {
            throw new InvalidOperationException("FFmpeg transcoding is not enabled for media previews.");
        }

        var ffmpegStatus = await dataService.GetFfmpegStatusAsync(cancellationToken);
        if (!ffmpegStatus.FfmpegAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available on this LMS host.");
        }

        var streamResult = await dataService.OpenStreamAsync(itemId, cancellationToken);
        await streamResult.ContentStream.DisposeAsync();

        if (streamResult.Item.MediaKind != MediaKind.Video)
        {
            throw new InvalidOperationException("Only video media items can use HLS browser preview.");
        }

        var durationSeconds = streamResult.Item.DurationSeconds is > 0
            ? streamResult.Item.DurationSeconds.Value
            : await ProbeDurationAsync(ffmpegStatus.FfprobePath, streamResult.Item.FullPath, cancellationToken);
        if (durationSeconds <= 0)
        {
            throw new InvalidOperationException("This video has no known duration, so seekable HLS preview is unavailable. Run media metadata probing and try again.");
        }

        return (streamResult.Item, durationSeconds);
    }

    private async Task<MediaHlsPreviewSegmentResult> OpenVodHlsSegmentAsync(
        Guid itemId,
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        var settings = await dataService.GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException("The Media Library Streaming Integration is disabled.");
        }

        if (settings.StreamingMode != MediaStreamingMode.TranscodeFallback || !settings.EnableTranscoding)
        {
            throw new InvalidOperationException("FFmpeg transcoding is not enabled for media previews.");
        }

        var ffmpegStatus = await dataService.GetFfmpegStatusAsync(cancellationToken);
        if (!ffmpegStatus.FfmpegAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available on this LMS host.");
        }

        var streamResult = await dataService.OpenStreamAsync(itemId, cancellationToken);
        await streamResult.ContentStream.DisposeAsync();

        if (streamResult.Item.MediaKind != MediaKind.Video)
        {
            throw new InvalidOperationException("Only video media items can use HLS browser preview.");
        }

        var durationSeconds = streamResult.Item.DurationSeconds is > 0
            ? streamResult.Item.DurationSeconds.Value
            : await ProbeDurationAsync(ffmpegStatus.FfprobePath, streamResult.Item.FullPath, cancellationToken);
        var startSeconds = segmentIndex * HlsVodSegmentSeconds;
        if (segmentIndex < 0 || durationSeconds <= 0 || startSeconds >= durationSeconds)
        {
            throw new FileNotFoundException("Unknown media preview segment.");
        }

        var segmentSeconds = Math.Min(HlsVodSegmentSeconds, durationSeconds - startSeconds);
        var lease = await AcquireFfmpegLeaseAsync(settings.MaxConcurrentFfmpegJobs, cancellationToken);
        try
        {
            var (process, processState) = StartVodHlsSegmentFfmpeg(
                ffmpegStatus.FfmpegPath,
                streamResult.Item,
                startSeconds,
                segmentSeconds);
            return new MediaHlsPreviewSegmentResult(
                new FfmpegPreviewStream(process, process.StandardOutput.BaseStream, lease, logger, streamResult.Item.Id, processState),
                "video/mp2t");
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void CloseHlsSession(Guid itemId)
    {
        if (HlsSessions.TryRemove(itemId, out var session))
        {
            StopHlsSession(session);
        }
    }

    private static string BuildVodHlsPlaylist(MediaItem item, double durationSeconds, string? requestToken)
    {
        var tokenSuffix = string.IsNullOrWhiteSpace(requestToken)
            ? string.Empty
            : $"?token={Uri.EscapeDataString(requestToken)}";
        var segmentCount = Math.Max(1, (int)Math.Ceiling(durationSeconds / HlsVodSegmentSeconds));
        var targetDuration = Math.Max(1, (int)Math.Ceiling(HlsVodSegmentSeconds));
        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");
        builder.AppendLine("#EXT-X-VERSION:3");
        builder.AppendLine($"#EXT-X-TARGETDURATION:{targetDuration.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        builder.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        builder.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");

        for (var index = 0; index < segmentCount; index++)
        {
            var startSeconds = index * HlsVodSegmentSeconds;
            var segmentSeconds = Math.Min(HlsVodSegmentSeconds, durationSeconds - startSeconds);
            if (segmentSeconds <= 0)
            {
                break;
            }

            builder.Append("#EXTINF:")
                .Append(segmentSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .AppendLine(",");
            builder.Append("segment-")
                .Append(index.ToString("00000", CultureInfo.InvariantCulture))
                .Append(".ts")
                .AppendLine(tokenSuffix);
        }

        builder.AppendLine("#EXT-X-ENDLIST");
        return builder.ToString();
    }

    private async Task<HlsPreviewSession> EnsureHlsSessionAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (HlsSessions.TryGetValue(itemId, out var currentSession) &&
            currentSession.IsHealthy)
        {
            currentSession.ReleaseLeaseIfExited();
            currentSession.Touch();
            return currentSession;
        }

        await HlsSessionGate.WaitAsync(cancellationToken);
        try
        {
            CleanupIdleHlsSessions(DateTimeOffset.UtcNow);
            if (HlsSessions.TryGetValue(itemId, out var existingSession) &&
                existingSession.IsHealthy)
            {
                existingSession.ReleaseLeaseIfExited();
                existingSession.Touch();
                return existingSession;
            }

            if (existingSession is not null)
            {
                StopHlsSession(existingSession);
                HlsSessions.TryRemove(itemId, out _);
            }

            var settings = await dataService.GetSettingsAsync(cancellationToken);
            if (!settings.IsEnabled)
            {
                throw new InvalidOperationException("The Media Library Streaming Integration is disabled.");
            }

            if (settings.StreamingMode != MediaStreamingMode.TranscodeFallback || !settings.EnableTranscoding)
            {
                throw new InvalidOperationException("FFmpeg transcoding is not enabled for media previews.");
            }

            var ffmpegStatus = await dataService.GetFfmpegStatusAsync(cancellationToken);
            if (!ffmpegStatus.FfmpegAvailable)
            {
                throw new InvalidOperationException("FFmpeg is not available on this LMS host.");
            }

            var streamResult = await dataService.OpenStreamAsync(itemId, cancellationToken);
            await streamResult.ContentStream.DisposeAsync();

            if (streamResult.Item.MediaKind != MediaKind.Video)
            {
                throw new InvalidOperationException("Only video media items can use HLS browser preview.");
            }

            var lease = await AcquireFfmpegLeaseAsync(settings.MaxConcurrentFfmpegJobs, cancellationToken);
            try
            {
                var directoryPath = PrepareHlsSessionDirectory(settings, itemId);
                var process = StartHlsFfmpeg(ffmpegStatus.FfmpegPath, streamResult.Item, directoryPath);
                var session = new HlsPreviewSession(itemId, directoryPath, process, lease);
                HlsSessions[itemId] = session;
                await WaitForHlsPlaylistReadyAsync(session, cancellationToken);
                return session;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            HlsSessionGate.Release();
        }
    }

    private static async Task<IDisposable> AcquireFfmpegLeaseAsync(int configuredMaxJobs, CancellationToken cancellationToken)
    {
        var maxJobs = Math.Clamp(configuredMaxJobs, 1, 8);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (FfmpegGate)
            {
                if (activeFfmpegJobs < maxJobs)
                {
                    activeFfmpegJobs++;
                    return new FfmpegJobLease();
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for an FFmpeg preview slot. Close the current preview or increase the maximum concurrent FFmpeg jobs.");
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    private (Process Process, FfmpegPreviewProcessState State) StartFfmpeg(string ffmpegPath, MediaItem item)
    {
        var processState = new FfmpegPreviewProcessState();
        var process = new Process
        {
            StartInfo = BuildFfmpegStartInfo(ffmpegPath, item),
            EnableRaisingEvents = true
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            logger.LogInformation("Started FFmpeg media preview transcode for item {ItemId}", item.Id);
            process.Exited += (_, _) =>
            {
                try
                {
                    if (!processState.WasStopped && process.ExitCode != 0)
                    {
                        logger.LogWarning(
                            "FFmpeg media preview transcode for item {ItemId} exited with code {ExitCode}: {Error}",
                            item.Id,
                            process.ExitCode,
                            stderr.ToString().Trim());
                    }
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not inspect FFmpeg preview process exit state for item {ItemId}.", item.Id);
                }
            };

            return (process, processState);
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException("FFmpeg could not be started. Check the configured FFmpeg path.", exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private (Process Process, FfmpegPreviewProcessState State) StartWebmFfmpeg(
        string ffmpegPath,
        MediaItem item,
        double? startSeconds)
    {
        var processState = new FfmpegPreviewProcessState();
        var process = new Process
        {
            StartInfo = BuildWebmFfmpegStartInfo(ffmpegPath, item, startSeconds),
            EnableRaisingEvents = true
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            logger.LogInformation("Started FFmpeg WebM media preview for item {ItemId}", item.Id);
            process.Exited += (_, _) =>
            {
                try
                {
                    if (!processState.WasStopped && process.ExitCode != 0)
                    {
                        logger.LogWarning(
                            "FFmpeg WebM media preview for item {ItemId} exited with code {ExitCode}: {Error}",
                            item.Id,
                            process.ExitCode,
                            stderr.ToString().Trim());
                    }
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not inspect FFmpeg WebM preview process exit state for item {ItemId}.", item.Id);
                }
            };

            return (process, processState);
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException("FFmpeg could not be started. Check the configured FFmpeg path.", exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private (Process Process, FfmpegPreviewProcessState State) StartVodHlsSegmentFfmpeg(
        string ffmpegPath,
        MediaItem item,
        double startSeconds,
        double segmentSeconds)
    {
        var processState = new FfmpegPreviewProcessState();
        var process = new Process
        {
            StartInfo = BuildVodHlsSegmentFfmpegStartInfo(ffmpegPath, item, startSeconds, segmentSeconds),
            EnableRaisingEvents = true
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            logger.LogDebug(
                "Started FFmpeg VOD HLS segment {StartSeconds}s for item {ItemId}",
                startSeconds,
                item.Id);
            process.Exited += (_, _) =>
            {
                try
                {
                    if (!processState.WasStopped && process.ExitCode != 0)
                    {
                        logger.LogWarning(
                            "FFmpeg VOD HLS segment for item {ItemId} exited with code {ExitCode}: {Error}",
                            item.Id,
                            process.ExitCode,
                            stderr.ToString().Trim());
                    }
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not inspect FFmpeg VOD HLS segment process exit state for item {ItemId}.", item.Id);
                }
            };

            return (process, processState);
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException("FFmpeg could not be started. Check the configured FFmpeg path.", exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private Process StartHlsFfmpeg(string ffmpegPath, MediaItem item, string directoryPath)
    {
        var process = new Process
        {
            StartInfo = BuildHlsFfmpegStartInfo(ffmpegPath, item, directoryPath),
            EnableRaisingEvents = true
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            logger.LogInformation("Started FFmpeg HLS media preview for item {ItemId}", item.Id);
            process.Exited += (_, _) =>
            {
                try
                {
                    if (process.ExitCode != 0)
                    {
                        logger.LogWarning(
                            "FFmpeg HLS media preview for item {ItemId} exited with code {ExitCode}: {Error}",
                            item.Id,
                            process.ExitCode,
                            stderr.ToString().Trim());
                    }
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not inspect FFmpeg HLS preview process exit state for item {ItemId}.", item.Id);
                }
            };

            return process;
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException("FFmpeg could not be started. Check the configured FFmpeg path.", exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static ProcessStartInfo BuildFfmpegStartInfo(string ffmpegPath, MediaItem item)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddCommonArguments(startInfo, item.FullPath);
        if (item.MediaKind == MediaKind.Audio)
        {
            AddAudioOnlyArguments(startInfo);
            AddFragmentedMp4OutputArguments(startInfo);
        }
        else
        {
            AddVideoArguments(startInfo);
            AddFragmentedMp4OutputArguments(startInfo);
        }

        return startInfo;
    }

    private static ProcessStartInfo BuildHlsFfmpegStartInfo(string ffmpegPath, MediaItem item, string directoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
            WorkingDirectory = directoryPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddCommonArguments(startInfo, item.FullPath);
        AddVideoArguments(startInfo);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("hls");
        startInfo.ArgumentList.Add("-hls_time");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-hls_list_size");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-hls_playlist_type");
        startInfo.ArgumentList.Add("event");
        startInfo.ArgumentList.Add("-hls_segment_type");
        startInfo.ArgumentList.Add("fmp4");
        startInfo.ArgumentList.Add("-hls_fmp4_init_filename");
        startInfo.ArgumentList.Add("init.mp4");
        startInfo.ArgumentList.Add("-hls_flags");
        startInfo.ArgumentList.Add("independent_segments");
        startInfo.ArgumentList.Add("-hls_segment_filename");
        startInfo.ArgumentList.Add("segment-%05d.m4s");
        startInfo.ArgumentList.Add("index.m3u8");
        return startInfo;
    }

    private static ProcessStartInfo BuildWebmFfmpegStartInfo(string ffmpegPath, MediaItem item, double? startSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddCommonArguments(startInfo, item.FullPath, startSeconds);
        AddWebmVideoArguments(startInfo);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("webm");
        startInfo.ArgumentList.Add("pipe:1");
        return startInfo;
    }

    private static ProcessStartInfo BuildVodHlsSegmentFfmpegStartInfo(
        string ffmpegPath,
        MediaItem item,
        double startSeconds,
        double segmentSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddCommonArguments(startInfo, item.FullPath, startSeconds);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(segmentSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        AddVideoArguments(startInfo);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mpegts");
        startInfo.ArgumentList.Add("-mpegts_flags");
        startInfo.ArgumentList.Add("resend_headers");
        startInfo.ArgumentList.Add("-muxdelay");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-muxpreload");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("pipe:1");
        return startInfo;
    }

    private static void AddFragmentedMp4OutputArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("frag_keyframe+empty_moov+default_base_moof");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add("pipe:1");
    }

    private static void AddCommonArguments(ProcessStartInfo startInfo, string fullPath, double? startSeconds = null)
    {
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("+genpts");
        if (startSeconds is > 0)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(Math.Max(0, startSeconds.Value).ToString("0.###", CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(fullPath);
    }

    private static string PrepareHlsSessionDirectory(MediaLibraryIntegrationSettings settings, Guid itemId)
    {
        var cacheRoot = string.IsNullOrWhiteSpace(settings.TempCacheFolder)
            ? Path.Combine(Path.GetTempPath(), "linuxmadesane", "media-preview-hls")
            : Path.Combine(settings.TempCacheFolder, "media-preview-hls");
        var directoryPath = Path.Combine(cacheRoot, itemId.ToString("N"));

        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static async Task WaitForHlsPlaylistReadyAsync(HlsPreviewSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Process.HasExited)
            {
                throw new InvalidOperationException("FFmpeg stopped before the media preview became ready.");
            }

            if (File.Exists(session.PlaylistPath))
            {
                var playlist = await File.ReadAllTextAsync(session.PlaylistPath, cancellationToken);
                var firstSegment = Directory
                    .EnumerateFiles(session.DirectoryPath, "segment-*.m4s", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (File.Exists(Path.Combine(session.DirectoryPath, "init.mp4")) &&
                    playlist.Contains(".m4s", StringComparison.OrdinalIgnoreCase) &&
                    firstSegment is not null)
                {
                    return;
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for the media preview stream to become ready.");
    }

    private async Task<double> ProbeDurationAsync(
        string ffprobePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-show_entries");
        process.StartInfo.ArgumentList.Add("format=duration");
        process.StartInfo.ArgumentList.Add("-of");
        process.StartInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        process.StartInfo.ArgumentList.Add(fullPath);

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);

            var output = (await outputTask).Trim();
            if (process.ExitCode == 0 &&
                double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds))
            {
                return durationSeconds;
            }

            var error = (await errorTask).Trim();
            logger.LogWarning("FFprobe could not read media duration for {Path}: {Error}", fullPath, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
            }

            logger.LogWarning("Timed out probing media duration for {Path}", fullPath);
        }
        catch (Win32Exception exception)
        {
            logger.LogWarning(exception, "FFprobe could not be started while probing media duration.");
        }
        finally
        {
            process.Dispose();
        }

        return 0;
    }

    private static string RewritePlaylistSegmentUrls(string playlist, string? requestToken)
    {
        var tokenSuffix = string.IsNullOrWhiteSpace(requestToken)
            ? string.Empty
            : $"?token={Uri.EscapeDataString(requestToken)}";
        var builder = new StringBuilder();
        using var reader = new StringReader(playlist);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("#EXT-X-MAP:URI=\"init.mp4\"", StringComparison.Ordinal))
            {
                builder.AppendLine(line.Replace("init.mp4", $"init.mp4{tokenSuffix}", StringComparison.Ordinal));
                continue;
            }

            builder.AppendLine(HlsSegmentNamePattern.IsMatch(line) ? $"{line}{tokenSuffix}" : line);
        }

        return builder.ToString();
    }

    private static void CleanupIdleHlsSessions(DateTimeOffset now)
    {
        foreach (var session in HlsSessions)
        {
            session.Value.ReleaseLeaseIfExited();
            if (now - session.Value.LastAccessUtc < TimeSpan.FromMinutes(2) &&
                File.Exists(session.Value.PlaylistPath))
            {
                continue;
            }

            if (HlsSessions.TryRemove(session.Key, out var removed))
            {
                StopHlsSession(removed);
            }
        }
    }

    private static void StopHlsSession(HlsPreviewSession session)
    {
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            session.Process.Dispose();
            session.Lease.Dispose();
            if (Directory.Exists(session.DirectoryPath))
            {
                Directory.Delete(session.DirectoryPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void AddVideoArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0?");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add("scale=w=min(960\\,iw):h=-2");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("ultrafast");
        startInfo.ArgumentList.Add("-tune");
        startInfo.ArgumentList.Add("zerolatency");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("30");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add("48");
        startInfo.ArgumentList.Add("-keyint_min");
        startInfo.ArgumentList.Add("48");
        startInfo.ArgumentList.Add("-sc_threshold");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-bf");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("128k");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
    }

    private static void AddWebmVideoArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0?");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add("scale=w=min(960\\,iw):h=-2");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libvpx");
        startInfo.ArgumentList.Add("-deadline");
        startInfo.ArgumentList.Add("realtime");
        startInfo.ArgumentList.Add("-cpu-used");
        startInfo.ArgumentList.Add("8");
        startInfo.ArgumentList.Add("-b:v");
        startInfo.ArgumentList.Add("1200k");
        startInfo.ArgumentList.Add("-maxrate");
        startInfo.ArgumentList.Add("1600k");
        startInfo.ArgumentList.Add("-bufsize");
        startInfo.ArgumentList.Add("3200k");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add("48");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("libopus");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("96k");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-cluster_time_limit");
        startInfo.ArgumentList.Add("1000");
    }

    private static void AddAudioOnlyArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("160k");
    }

    private sealed class FfmpegPreviewProcessState
    {
        public bool WasStopped { get; set; }
    }

    private sealed class HlsPreviewSession
    {
        public HlsPreviewSession(Guid itemId, string directoryPath, Process process, IDisposable lease)
        {
            ItemId = itemId;
            DirectoryPath = directoryPath;
            PlaylistPath = Path.Combine(directoryPath, "index.m3u8");
            Process = process;
            Lease = lease;
            Process.Exited += (_, _) => Lease.Dispose();
        }

        public Guid ItemId { get; }
        public string DirectoryPath { get; }
        public string PlaylistPath { get; }
        public Process Process { get; }
        public IDisposable Lease { get; }
        public DateTimeOffset LastAccessUtc { get; private set; } = DateTimeOffset.UtcNow;
        public bool IsHealthy => File.Exists(PlaylistPath);

        public void Touch()
        {
            LastAccessUtc = DateTimeOffset.UtcNow;
        }

        public void ReleaseLeaseIfExited()
        {
            try
            {
                if (Process.HasExited)
                {
                    Lease.Dispose();
                }
            }
            catch
            {
            }
        }
    }

    private sealed class FfmpegJobLease : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (FfmpegGate)
            {
                activeFfmpegJobs = Math.Max(0, activeFfmpegJobs - 1);
            }
        }
    }

    private sealed class FfmpegPreviewStream(
        Process process,
        Stream outputStream,
        IDisposable lease,
        ILogger logger,
        Guid itemId,
        FfmpegPreviewProcessState processState) : Stream
    {
        private bool disposed;

        public override bool CanRead => outputStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            outputStream.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            outputStream.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (disposing)
            {
                StopProcess();
                outputStream.Dispose();
                process.Dispose();
                lease.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopProcess();
            await outputStream.DisposeAsync();
            process.Dispose();
            lease.Dispose();
            await base.DisposeAsync();
        }

        private void StopProcess()
        {
            try
            {
                if (!process.HasExited)
                {
                    processState.WasStopped = true;
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "FFmpeg media preview process for item {ItemId} was already stopped.", itemId);
            }
        }
    }
}

public sealed record MediaTranscodePreviewResult(Stream ContentStream, string ContentType);
public sealed record MediaHlsPreviewPlaylistResult(string Playlist, string ContentType);
public sealed record MediaHlsPreviewSegmentResult(Stream ContentStream, string ContentType);
