// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AiProviderModelDiscoveryService(
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOllamaRuntimeService ollamaRuntimeService) : IAiProviderModelDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AiProviderModelOption>> DiscoverAsync(
        AiProviderSettings settings,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default) =>
        settings.ProviderType switch
        {
            AiProviderType.OpenAi => DiscoverOpenAiModelsAsync(settings, apiKeyOverride, cancellationToken),
            AiProviderType.Anthropic => DiscoverAnthropicModelsAsync(settings, apiKeyOverride, cancellationToken),
            AiProviderType.Gemini => DiscoverGeminiModelsAsync(settings, apiKeyOverride, cancellationToken),
            AiProviderType.Groq => DiscoverGroqModelsAsync(settings, apiKeyOverride, cancellationToken),
            AiProviderType.XAi => DiscoverXAiModelsAsync(settings, apiKeyOverride, cancellationToken),
            AiProviderType.DeepSeek => DiscoverDeepSeekModelsAsync(settings, apiKeyOverride, cancellationToken),
            AiProviderType.Ollama => DiscoverOllamaModelsAsync(cancellationToken),
            _ => Task.FromResult<IReadOnlyList<AiProviderModelOption>>([])
        };

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverOpenAiModelsAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(settings, apiKeyOverride, "OpenAI", cancellationToken);
        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, ResolveOpenAiModelsEndpoint(settings));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, body, "OpenAI");

        var data = JsonNode.Parse(body)?["data"] as JsonArray ?? [];
        return data
            .OfType<JsonObject>()
            .Select(item => item["id"]?.GetValue<string>() ?? string.Empty)
            .Where(IsOpenAiTextModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsLikelyLatestOpenAiModel)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new AiProviderModelOption(
                AiProviderType.OpenAi,
                id,
                BuildDisplayName(id),
                "Discovered from the OpenAI models API for this API key.",
                true,
                false))
            .ToArray();
    }

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverAnthropicModelsAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(settings, apiKeyOverride, "Anthropic", cancellationToken);
        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, ResolveAnthropicModelsEndpoint(settings));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Add("x-api-key", apiKey.Trim());
        message.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, body, "Anthropic");

        var data = JsonNode.Parse(body)?["data"] as JsonArray ?? [];
        return data
            .OfType<JsonObject>()
            .Select(item =>
            {
                var id = item["id"]?.GetValue<string>() ?? string.Empty;
                var displayName = item["display_name"]?.GetValue<string>() ?? BuildDisplayName(id);
                return new { id, displayName };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.id))
            .DistinctBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AiProviderModelOption(
                AiProviderType.Anthropic,
                item.id,
                item.displayName,
                "Discovered from the Anthropic models API for this API key.",
                true,
                false))
            .ToArray();
    }

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverGeminiModelsAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(settings, apiKeyOverride, "Gemini", cancellationToken);
        using var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.GetAsync(ResolveGeminiModelsEndpoint(settings, apiKey), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, body, "Gemini");

        var models = JsonNode.Parse(body)?["models"] as JsonArray ?? [];
        return models
            .OfType<JsonObject>()
            .Select(item =>
            {
                var rawName = item["name"]?.GetValue<string>() ?? string.Empty;
                var id = rawName.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                    ? rawName["models/".Length..]
                    : rawName;
                var displayName = item["displayName"]?.GetValue<string>() ?? BuildDisplayName(id);
                var methods = item["supportedGenerationMethods"] as JsonArray ?? [];
                var supportsGenerateContent = methods
                    .OfType<JsonValue>()
                    .Select(value => value.GetValue<string>())
                    .Any(method => method.Equals("generateContent", StringComparison.OrdinalIgnoreCase));

                return new { id, displayName, supportsGenerateContent };
            })
            .Where(item => IsGeminiTextModel(item.id) && item.supportsGenerateContent)
            .DistinctBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AiProviderModelOption(
                AiProviderType.Gemini,
                item.id,
                item.displayName,
                "Discovered from the Gemini models API for this API key.",
                true,
                false))
            .ToArray();
    }

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverGroqModelsAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(settings, apiKeyOverride, "Groq", cancellationToken);
        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, ResolveGroqModelsEndpoint(settings));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, body, "Groq");

        var data = JsonNode.Parse(body)?["data"] as JsonArray ?? [];
        return data
            .OfType<JsonObject>()
            .Select(item => item["id"]?.GetValue<string>() ?? string.Empty)
            .Where(IsGroqTextModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsLikelyRecommendedGroqModel)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new AiProviderModelOption(
                AiProviderType.Groq,
                id,
                BuildDisplayName(id),
                "Discovered from the Groq models API for this API key.",
                true,
                false))
            .ToArray();
    }

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverXAiModelsAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(settings, apiKeyOverride, "xAI Grok", cancellationToken);
        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, ResolveXAiModelsEndpoint(settings));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, body, "xAI Grok");

        var data = JsonNode.Parse(body)?["data"] as JsonArray ?? [];
        return data
            .OfType<JsonObject>()
            .Select(item => item["id"]?.GetValue<string>() ?? string.Empty)
            .Where(IsXAiTextModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsLikelyRecommendedXAiModel)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new AiProviderModelOption(
                AiProviderType.XAi,
                id,
                BuildDisplayName(id),
                "Discovered from the xAI models API for this API key.",
                true,
                false))
            .ToArray();
    }

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverDeepSeekModelsAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(settings, apiKeyOverride, "DeepSeek", cancellationToken);
        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, ResolveDeepSeekModelsEndpoint(settings));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, body, "DeepSeek");

        var data = JsonNode.Parse(body)?["data"] as JsonArray ?? [];
        return data
            .OfType<JsonObject>()
            .Select(item => item["id"]?.GetValue<string>() ?? string.Empty)
            .Where(IsDeepSeekTextModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsLikelyRecommendedDeepSeekModel)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new AiProviderModelOption(
                AiProviderType.DeepSeek,
                id,
                BuildDisplayName(id),
                "Discovered from the DeepSeek models API for this API key.",
                true,
                false))
            .ToArray();
    }

    private async Task<IReadOnlyList<AiProviderModelOption>> DiscoverOllamaModelsAsync(CancellationToken cancellationToken)
    {
        var installedModels = await ollamaRuntimeService.ListInstalledModelsAsync(cancellationToken);
        return installedModels
            .Select(model => new AiProviderModelOption(
                AiProviderType.Ollama,
                model.ModelId,
                model.DisplayName,
                "Discovered from the local Ollama runtime.",
                model.Capabilities.HasFlag(AiProviderCapabilityFlag.ToolCalling),
                model.IsDefault))
            .ToArray();
    }

    private async Task<string> ResolveApiKeyAsync(
        AiProviderSettings settings,
        string? apiKeyOverride,
        string providerName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(apiKeyOverride))
        {
            return apiKeyOverride.Trim();
        }

        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKeySecretReference)
            ? null
            : await secretStore.ResolveSecretAsync(settings.ApiKeySecretReference, cancellationToken);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{providerName} model refresh needs an API key.");
        }

        return apiKey;
    }

    private static Uri ResolveOpenAiModelsEndpoint(AiProviderSettings settings) =>
        string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? new Uri("https://api.openai.com/v1/models", UriKind.Absolute)
            : new Uri(new Uri(settings.BaseUrl.Trim(), UriKind.Absolute), "/v1/models");

    private static Uri ResolveAnthropicModelsEndpoint(AiProviderSettings settings) =>
        string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? new Uri("https://api.anthropic.com/v1/models", UriKind.Absolute)
            : new Uri(new Uri(settings.BaseUrl.Trim(), UriKind.Absolute), "/v1/models");

    private static Uri ResolveGeminiModelsEndpoint(AiProviderSettings settings, string apiKey)
    {
        var endpoint = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? new Uri("https://generativelanguage.googleapis.com/v1beta/models", UriKind.Absolute)
            : new Uri(new Uri(settings.BaseUrl.Trim(), UriKind.Absolute), "/v1beta/models");
        var separator = string.IsNullOrEmpty(endpoint.Query) ? "?" : "&";
        return new Uri($"{endpoint}{separator}key={Uri.EscapeDataString(apiKey.Trim())}", UriKind.Absolute);
    }

    private static Uri ResolveGroqModelsEndpoint(AiProviderSettings settings) =>
        string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? new Uri("https://api.groq.com/openai/v1/models", UriKind.Absolute)
            : GroqAiProvider.ResolveOpenAiCompatibleEndpoint(settings.BaseUrl, "models");

    private static Uri ResolveXAiModelsEndpoint(AiProviderSettings settings) =>
        string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? new Uri("https://api.x.ai/v1/models", UriKind.Absolute)
            : XAiGrokAiProvider.ResolveOpenAiCompatibleEndpoint(settings.BaseUrl, "models");

    private static Uri ResolveDeepSeekModelsEndpoint(AiProviderSettings settings) =>
        string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? new Uri("https://api.deepseek.com/models", UriKind.Absolute)
            : DeepSeekAiProvider.ResolveOpenAiCompatibleEndpoint(settings.BaseUrl, "models");

    private static void EnsureResponseSucceeded(HttpResponseMessage response, string responseBody, string providerName)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorMessage = TryExtractErrorMessage(responseBody);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(errorMessage)
                ? $"{providerName} returned {(int)response.StatusCode} {response.ReasonPhrase} while refreshing models."
                : errorMessage);
    }

    private static string? TryExtractErrorMessage(string responseBody)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonObject>(responseBody, JsonOptions);
            return root?["error"] switch
            {
                JsonObject errorObject => errorObject["message"]?.GetValue<string>(),
                JsonValue errorValue => errorValue.GetValue<string>(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsOpenAiTextModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        if (normalized.Contains("audio", StringComparison.Ordinal) ||
            normalized.Contains("embedding", StringComparison.Ordinal) ||
            normalized.Contains("image", StringComparison.Ordinal) ||
            normalized.Contains("moderation", StringComparison.Ordinal) ||
            normalized.Contains("realtime", StringComparison.Ordinal) ||
            normalized.Contains("sora", StringComparison.Ordinal) ||
            normalized.Contains("transcribe", StringComparison.Ordinal) ||
            normalized.Contains("tts", StringComparison.Ordinal) ||
            normalized.Contains("whisper", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsOpenAiDatedSnapshot(normalized))
        {
            return false;
        }

        return normalized.StartsWith("gpt-", StringComparison.Ordinal) ||
               normalized.StartsWith("o1", StringComparison.Ordinal) ||
               normalized.StartsWith("o3", StringComparison.Ordinal) ||
               normalized.StartsWith("o4", StringComparison.Ordinal) ||
               normalized.StartsWith("codex-", StringComparison.Ordinal);
    }

    private static bool IsLikelyLatestOpenAiModel(string modelId) =>
        modelId.StartsWith("gpt-5.5", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("gpt-5.4", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("gpt-5.2", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenAiDatedSnapshot(string normalizedModelId)
    {
        var segments = normalizedModelId.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 4 &&
               IsDigits(segments[^3], 4) &&
               IsDigits(segments[^2], 2) &&
               IsDigits(segments[^1], 2);
    }

    private static bool IsDigits(string value, int expectedLength) =>
        value.Length == expectedLength && value.All(char.IsDigit);

    private static bool IsGeminiTextModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        return normalized.StartsWith("gemini-", StringComparison.Ordinal) &&
               !normalized.Contains("embedding", StringComparison.Ordinal) &&
               !normalized.Contains("image", StringComparison.Ordinal) &&
               !normalized.Contains("live", StringComparison.Ordinal) &&
               !normalized.Contains("tts", StringComparison.Ordinal);
    }

    private static bool IsGroqTextModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        return !normalized.Contains("audio", StringComparison.Ordinal) &&
               !normalized.Contains("embedding", StringComparison.Ordinal) &&
               !normalized.Contains("guard", StringComparison.Ordinal) &&
               !normalized.Contains("image", StringComparison.Ordinal) &&
               !normalized.Contains("moderation", StringComparison.Ordinal) &&
               !normalized.Contains("tts", StringComparison.Ordinal) &&
               !normalized.Contains("whisper", StringComparison.Ordinal);
    }

    private static bool IsLikelyRecommendedGroqModel(string modelId) =>
        modelId.Equals("llama-3.3-70b-versatile", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("openai/gpt-oss-120b", StringComparison.OrdinalIgnoreCase);

    private static bool IsXAiTextModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        return normalized.StartsWith("grok-", StringComparison.Ordinal) &&
               !normalized.Contains("audio", StringComparison.Ordinal) &&
               !normalized.Contains("embedding", StringComparison.Ordinal) &&
               !normalized.Contains("image", StringComparison.Ordinal) &&
               !normalized.Contains("imagine", StringComparison.Ordinal) &&
               !normalized.Contains("tts", StringComparison.Ordinal) &&
               !normalized.Contains("vision", StringComparison.Ordinal);
    }

    private static bool IsLikelyRecommendedXAiModel(string modelId) =>
        modelId.Equals("grok-4.3", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeepSeekTextModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        return normalized.StartsWith("deepseek-", StringComparison.Ordinal) &&
               !normalized.Contains("embedding", StringComparison.Ordinal) &&
               !normalized.Contains("fim", StringComparison.Ordinal) &&
               !normalized.Contains("image", StringComparison.Ordinal);
    }

    private static bool IsLikelyRecommendedDeepSeekModel(string modelId) =>
        modelId.Equals("deepseek-v4-flash", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("deepseek-v4-pro", StringComparison.OrdinalIgnoreCase);

    private static string BuildDisplayName(string modelId) =>
        string.Join(
            ' ',
            modelId
                .Replace('/', '-')
                .Replace(':', '-')
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length <= 3 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));
}
