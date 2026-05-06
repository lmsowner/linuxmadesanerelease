using System.Text;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Models.MediaLibrary;
using LinuxMadeSane.Web.Services;
using Microsoft.Net.Http.Headers;

namespace LinuxMadeSane.Web;

public static class MediaLibraryApiEndpointExtensions
{
    private const string VlcPlaylistContentType = "audio/x-mpegurl; charset=utf-8";

    public static IEndpointRouteBuilder MapMediaLibraryIntegrationApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/integrations/media-library");

        group.MapGet("/playlists/all.m3u8", async (
            HttpContext context,
            IMediaLibraryIntegrationService mediaLibrary,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!await IsPlaylistRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, "all"))
            {
                return Results.Challenge();
            }

            var request = new MediaPlaylistRequest([], null, null, true, MediaSortMode.Path, MediaPlaylistOutputFormat.M3U8);
            return await BuildPlaylistResultAsync(context, mediaLibrary, signedUrls, settings, request, "all-media.m3u8", cancellationToken);
        });

        group.MapGet("/playlists/all-media.m3u8", async (
            HttpContext context,
            IMediaLibraryIntegrationService mediaLibrary,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!await IsPlaylistRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, "all"))
            {
                return Results.Challenge();
            }

            var request = new MediaPlaylistRequest([], null, null, true, MediaSortMode.Path, MediaPlaylistOutputFormat.M3U8);
            return await BuildPlaylistResultAsync(context, mediaLibrary, signedUrls, settings, request, "all-media.m3u8", cancellationToken);
        });

        group.MapGet("/playlists/root/{rootId:guid}.m3u8", async (
            HttpContext context,
            Guid rootId,
            IMediaLibraryIntegrationService mediaLibrary,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            var scope = $"root:{rootId:N}";
            if (!await IsPlaylistRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, scope))
            {
                return Results.Challenge();
            }

            var request = new MediaPlaylistRequest([rootId], null, null, true, MediaSortMode.Path, MediaPlaylistOutputFormat.M3U8);
            return await BuildPlaylistResultAsync(context, mediaLibrary, signedUrls, settings, request, $"media-root-{rootId:N}.m3u8", cancellationToken);
        });

        group.MapGet("/playlists/root/{rootId:guid}/{fileName}", async (
            HttpContext context,
            Guid rootId,
            string fileName,
            IMediaLibraryIntegrationService mediaLibrary,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            if (!fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            var scope = $"root:{rootId:N}";
            if (!await IsPlaylistRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, scope))
            {
                return Results.Challenge();
            }

            var request = new MediaPlaylistRequest([rootId], null, null, true, MediaSortMode.Path, MediaPlaylistOutputFormat.M3U8);
            return await BuildPlaylistResultAsync(
                context,
                mediaLibrary,
                signedUrls,
                settings,
                request,
                NormalizePlaylistFileName(fileName, $"media-root-{rootId:N}.m3u8"),
                cancellationToken);
        });

        group.MapGet("/playlists/category/{category}.m3u8", async (
            HttpContext context,
            string category,
            IMediaLibraryIntegrationService mediaLibrary,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<MediaLibraryCategory>(category, ignoreCase: true, out var parsedCategory))
            {
                return Results.BadRequest("Unknown media category.");
            }

            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            var scope = $"category:{parsedCategory}";
            if (!await IsPlaylistRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, scope))
            {
                return Results.Challenge();
            }

            var request = new MediaPlaylistRequest([], parsedCategory, null, true, MediaSortMode.Path, MediaPlaylistOutputFormat.M3U8);
            return await BuildPlaylistResultAsync(context, mediaLibrary, signedUrls, settings, request, $"media-{parsedCategory}.m3u8", cancellationToken);
        });

        group.MapGet("/items/{itemId:guid}/stream", async (
            HttpContext context,
            Guid itemId,
            IMediaLibraryIntegrationService mediaLibrary,
            LinuxMadeSane.Core.Abstractions.IMediaLibraryIntegrationDataService dataService,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!settings.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!await IsStreamRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, itemId))
            {
                return Results.Challenge();
            }

            try
            {
                var result = await dataService.OpenStreamAsync(itemId, cancellationToken);
                return Results.File(
                    result.ContentStream,
                    result.ContentType,
                    fileDownloadName: null,
                    lastModified: result.LastModifiedUtc,
                    enableRangeProcessing: true);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (TimeoutException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapGet("/items/{itemId:guid}/preview-stream", async (
            HttpContext context,
            Guid itemId,
            IMediaLibraryIntegrationService mediaLibrary,
            MediaLibraryTranscodePreviewService transcodePreview,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!settings.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!await IsStreamRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, itemId))
            {
                return Results.Challenge();
            }

            try
            {
                var result = await transcodePreview.OpenPreviewStreamAsync(itemId, cancellationToken);
                return Results.File(
                    result.ContentStream,
                    result.ContentType,
                    fileDownloadName: null,
                    enableRangeProcessing: false);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (TimeoutException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapGet("/items/{itemId:guid}/preview-hls/index.m3u8", async (
            HttpContext context,
            Guid itemId,
            IMediaLibraryIntegrationService mediaLibrary,
            MediaLibraryTranscodePreviewService transcodePreview,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!settings.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!await IsStreamRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, itemId))
            {
                return Results.Challenge();
            }

            try
            {
                var result = await transcodePreview.OpenHlsPlaylistAsync(
                    itemId,
                    context.Request.Query["token"].ToString(),
                    cancellationToken);
                context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                return Results.Text(result.Playlist, result.ContentType, Encoding.UTF8);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (TimeoutException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapGet("/items/{itemId:guid}/preview-webm", async (
            HttpContext context,
            Guid itemId,
            double? start,
            IMediaLibraryIntegrationService mediaLibrary,
            MediaLibraryTranscodePreviewService transcodePreview,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!settings.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!await IsStreamRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, itemId))
            {
                return Results.Challenge();
            }

            try
            {
                var result = await transcodePreview.OpenWebmPreviewStreamAsync(itemId, start, cancellationToken);
                context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                return Results.File(
                    result.ContentStream,
                    result.ContentType,
                    fileDownloadName: null,
                    enableRangeProcessing: false);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (TimeoutException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        group.MapGet("/items/{itemId:guid}/preview-hls/{segmentName}", async (
            HttpContext context,
            Guid itemId,
            string segmentName,
            IMediaLibraryIntegrationService mediaLibrary,
            MediaLibraryTranscodePreviewService transcodePreview,
            ITrustedNetworkAccessService trustedNetworkAccess,
            MediaLibrarySignedUrlService signedUrls,
            CancellationToken cancellationToken) =>
        {
            var settings = (await mediaLibrary.GetDashboardAsync(cancellationToken)).Settings;
            if (!settings.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!await IsStreamRequestAllowedAsync(context, settings, trustedNetworkAccess, signedUrls, itemId))
            {
                return Results.Challenge();
            }

            try
            {
                var result = await transcodePreview.OpenHlsSegmentAsync(itemId, segmentName, cancellationToken);
                context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                return Results.File(
                    result.ContentStream,
                    result.ContentType,
                    fileDownloadName: null,
                    enableRangeProcessing: false);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (TimeoutException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        return endpoints;
    }

    private static async Task<IResult> BuildPlaylistResultAsync(
        HttpContext context,
        IMediaLibraryIntegrationService mediaLibrary,
        MediaLibrarySignedUrlService signedUrls,
        MediaLibraryIntegrationSettings settings,
        MediaPlaylistRequest request,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!settings.IsEnabled)
        {
            return Results.NotFound();
        }

        var items = await mediaLibrary.ListPlaylistItemsAsync(request, cancellationToken);
        var playlist = mediaLibrary.GeneratePlaylist(
            request,
            items,
            item => BuildStreamUrl(context, item.Id, settings, signedUrls));

        var downloadRequested =
            context.Request.Query.TryGetValue("download", out var download) &&
            string.Equals(download.ToString(), "1", StringComparison.Ordinal);
        var safeFileName = NormalizePlaylistFileName(fileName, "media-library.m3u8");
        context.Response.Headers[HeaderNames.ContentDisposition] =
            $"{(downloadRequested ? "attachment" : "inline")}; filename=\"{safeFileName}\"";
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";

        return Results.Bytes(Encoding.UTF8.GetBytes(playlist), VlcPlaylistContentType);
    }

    private static string NormalizePlaylistFileName(string fileName, string fallback)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? fallback : fileName);
        if (!name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            name = $"{Path.GetFileNameWithoutExtension(name)}.m3u8";
        }

        var safe = new string(name
            .Select(static character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());

        return string.IsNullOrWhiteSpace(safe)
            ? fallback
            : safe;
    }

    private static Task<bool> IsPlaylistRequestAllowedAsync(
        HttpContext context,
        MediaLibraryIntegrationSettings settings,
        ITrustedNetworkAccessService trustedNetworkAccess,
        MediaLibrarySignedUrlService signedUrls,
        string scope)
    {
        if (!settings.RequireLoginForPlaylists)
        {
            return Task.FromResult(true);
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return Task.FromResult(true);
        }

        var token = context.Request.Query["token"].ToString();
        if (settings.GenerateTemporarySignedStreamUrls && signedUrls.ValidatePlaylistToken(scope, token))
        {
            return Task.FromResult(true);
        }

        return IsAnonymousMediaRequestAllowedAsync(context, settings, trustedNetworkAccess);
    }

    private static async Task<bool> IsStreamRequestAllowedAsync(
        HttpContext context,
        MediaLibraryIntegrationSettings settings,
        ITrustedNetworkAccessService trustedNetworkAccess,
        MediaLibrarySignedUrlService signedUrls,
        Guid itemId)
    {
        if (!settings.RequireLoginForStreams)
        {
            return true;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        var token = context.Request.Query["token"].ToString();
        if (settings.GenerateTemporarySignedStreamUrls && signedUrls.ValidateStreamToken(itemId, token))
        {
            return true;
        }

        return await IsAnonymousMediaRequestAllowedAsync(context, settings, trustedNetworkAccess);
    }

    private static async Task<bool> IsAnonymousMediaRequestAllowedAsync(
        HttpContext context,
        MediaLibraryIntegrationSettings settings,
        ITrustedNetworkAccessService trustedNetworkAccess)
    {
        if (IsRemoteAddressInAllowlist(context, settings))
        {
            return true;
        }

        if (!settings.AllowLanAnonymousAccess)
        {
            return false;
        }

        var accessResult = await trustedNetworkAccess.EvaluateAsync(
            context.Connection.RemoteIpAddress,
            context.Request.Host.Host,
            context.RequestAborted);

        return accessResult.IsTrusted;
    }

    private static bool IsRemoteAddressInAllowlist(HttpContext context, MediaLibraryIntegrationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.IpAllowlistCsv))
        {
            return false;
        }

        var entries = settings.IpAllowlistCsv
            .Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(TrustedNetworkMatcher.IsValidAddressOrCidr)
            .Select((value, index) => new TrustedNetworkEntry(
                Guid.Empty,
                $"Media allowlist {index + 1}",
                value,
                string.Empty,
                true,
                true,
                false,
                false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow))
            .ToArray();

        return entries.Length > 0 && TrustedNetworkMatcher.Match(context.Connection.RemoteIpAddress, entries) is not null;
    }

    private static string BuildStreamUrl(
        HttpContext context,
        Guid itemId,
        MediaLibraryIntegrationSettings settings,
        MediaLibrarySignedUrlService signedUrls)
    {
        var pathBase = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : string.Empty;
        var url = $"{context.Request.Scheme}://{context.Request.Host}{pathBase}/api/integrations/media-library/items/{itemId:N}/stream";
        if (!settings.GenerateTemporarySignedStreamUrls)
        {
            return url;
        }

        var token = signedUrls.CreateStreamToken(itemId, TimeSpan.FromMinutes(settings.SignedUrlExpiryMinutes));
        return $"{url}?token={Uri.EscapeDataString(token)}";
    }
}
