// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public async Task<bool> IsSharedEnginePortAsync(
        int localPort,
        CancellationToken cancellationToken = default)
    {
        var settings = await localAiStore.GetSettingsAsync(cancellationToken);
        return Uri.TryCreate(settings.RuntimeEndpoint, UriKind.Absolute, out var runtimeEndpoint) &&
               runtimeEndpoint.Port == localPort;
    }

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

        var defaultModelId = settings.DefaultModelId.Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return new LocalAiPeerProxyResult(503, "application/json", "{\"error\":\"The host has no default Local AI model.\"}"u8.ToArray());
        }

        if (relativePath == "chat/completions")
        {
            requestBody = SetRequestModel(requestBody, defaultModelId);
            if (requestBody is null)
            {
                return new LocalAiPeerProxyResult(400, "application/json", "{\"error\":\"The AI request body must be a JSON object.\"}"u8.ToArray());
            }
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
            if (response.IsSuccessStatusCode && relativePath == "models")
            {
                body = SelectDefaultModel(body, defaultModelId);
                if (body is null)
                {
                    return new LocalAiPeerProxyResult(503, "application/json", "{\"error\":\"The host default Local AI model is not available.\"}"u8.ToArray());
                }
            }

            return new LocalAiPeerProxyResult((int)response.StatusCode, contentType, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ErrorResult(
                504,
                "The host Local AI model did not respond before its configured timeout. Open Local AI on the host and confirm the engine is ready, then try again.");
        }
        catch (HttpRequestException)
        {
            return ErrorResult(
                503,
                "The host could not reach its local Ollama API. Open Local AI on the host and start or repair the engine, then try again.");
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static LocalAiPeerProxyResult ErrorResult(int statusCode, string message) =>
        new(
            statusCode,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { error = message }));

    private static byte[]? SetRequestModel(byte[]? requestBody, string defaultModelId)
    {
        try
        {
            var request = requestBody is null
                ? null
                : JsonNode.Parse(requestBody) as JsonObject;
            if (request is null)
            {
                return null;
            }

            request["model"] = defaultModelId;
            return JsonSerializer.SerializeToUtf8Bytes(request);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? SelectDefaultModel(byte[] responseBody, string defaultModelId)
    {
        try
        {
            var response = JsonNode.Parse(responseBody) as JsonObject;
            var models = response?["data"] as JsonArray;
            var defaultModel = models?
                .OfType<JsonObject>()
                .FirstOrDefault(model => string.Equals(
                    model["id"]?.GetValue<string>(),
                    defaultModelId,
                    StringComparison.OrdinalIgnoreCase));
            if (response is null || defaultModel is null)
            {
                return null;
            }

            response["data"] = new JsonArray(defaultModel.DeepClone());
            return JsonSerializer.SerializeToUtf8Bytes(response);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
