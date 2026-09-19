// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Headers;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalAiPeerSharingService(
    LocalAiPeerSharingStore sharingStore,
    ILocalAiEngineStore localAiStore,
    IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "LocalAiPeerSharing";
    public const int MaximumRequestBytes = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim RequestGate = new(4, 4);

    public Task<LocalAiPeerSharingStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        sharingStore.GetStatusAsync(cancellationToken);

    public Task<LocalAiPeerSharingKeyResult> EnableWithNewKeyAsync(CancellationToken cancellationToken = default) =>
        sharingStore.EnableWithNewKeyAsync(cancellationToken);

    public Task DisableAsync(CancellationToken cancellationToken = default) =>
        sharingStore.DisableAsync(cancellationToken);

    public async Task<LocalAiPeerProxyResult?> ForwardAsync(
        string? bearerToken,
        string relativePath,
        byte[]? requestBody,
        CancellationToken cancellationToken = default)
    {
        if (!await sharingStore.ValidateAccessKeyAsync(bearerToken, cancellationToken))
        {
            return null;
        }

        if (requestBody?.Length > MaximumRequestBytes)
        {
            return new LocalAiPeerProxyResult(413, "application/json", "{\"error\":\"The AI request is too large.\"}"u8.ToArray());
        }

        if (relativePath is not ("models" or "chat/completions"))
        {
            return new LocalAiPeerProxyResult(404, "application/json", "{\"error\":\"Unsupported Local AI operation.\"}"u8.ToArray());
        }

        var settings = await localAiStore.GetSettingsAsync(cancellationToken);
        if (!Uri.TryCreate(settings.RuntimeEndpoint, UriKind.Absolute, out var runtimeEndpoint) ||
            runtimeEndpoint.Scheme is not ("http" or "https"))
        {
            return new LocalAiPeerProxyResult(503, "application/json", "{\"error\":\"The host Local AI runtime is not configured.\"}"u8.ToArray());
        }

        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 10, 3600)));
            using var request = new HttpRequestMessage(
                relativePath == "models" ? HttpMethod.Get : HttpMethod.Post,
                new Uri($"{runtimeEndpoint.ToString().TrimEnd('/')}/v1/{relativePath}"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (requestBody is not null)
            {
                request.Content = new ByteArrayContent(requestBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new LocalAiPeerProxyResult((int)response.StatusCode, contentType, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LocalAiPeerProxyResult(504, "application/json", "{\"error\":\"The host Local AI runtime timed out.\"}"u8.ToArray());
        }
        catch (HttpRequestException)
        {
            return new LocalAiPeerProxyResult(503, "application/json", "{\"error\":\"The host Local AI runtime is unavailable.\"}"u8.ToArray());
        }
        finally
        {
            RequestGate.Release();
        }
    }
}
