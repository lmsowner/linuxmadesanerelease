// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AiProviderConnectionTester(
    IAiProviderRegistry providerRegistry,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOllamaRuntimeService ollamaRuntimeService,
    IRemoteLmsAiEngineGateway remoteGateway) : IAiProviderConnectionTester
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AiProviderConnectionTestResult> TestAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = settings.ProviderType == AiProviderType.LinuxMadeSaneAiService
            ? TimeSpan.FromSeconds(130)
            : TimeSpan.FromSeconds(20);
        timeoutCts.CancelAfter(timeout);

        try
        {
            if (settings.ProviderType == AiProviderType.LinuxMadeSaneAiService)
            {
                return await TestLinuxMadeSaneAiServiceAsync(settings, checkedAtUtc, timeoutCts.Token);
            }

            var definition = providerRegistry.FindDefinition(settings.ProviderType);
            if (definition is null)
            {
                return new AiProviderConnectionTestResult(
                    false,
                    "Provider test failed.",
                    "The selected provider type is not supported.",
                    checkedAtUtc);
            }

            if (!definition.IsRuntimeImplemented)
            {
                return new AiProviderConnectionTestResult(
                    false,
                    "Provider test failed.",
                    string.IsNullOrWhiteSpace(definition.RuntimeNotes)
                        ? "That provider is not runnable in this build."
                        : definition.RuntimeNotes,
                    checkedAtUtc);
            }

            var provider = AiProviderRuntimeFactory.Create(
                definition,
                settings,
                BuildModels(settings),
                secretStore,
                httpClientFactory,
                ollamaRuntimeService,
                remoteGateway);

            var request = CreateRequest(settings, checkedAtUtc);
            var result = await provider.ExecuteTurnAsync(request, cancellationToken: timeoutCts.Token);

            var assistantText = string.Join(
                Environment.NewLine + Environment.NewLine,
                result.AssistantOutputs
                    .Select(output => output.Content?.Trim())
                    .Where(content => !string.IsNullOrWhiteSpace(content)));

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return new AiProviderConnectionTestResult(
                    false,
                    "Provider test failed.",
                    "The provider responded without any assistant text.",
                    checkedAtUtc);
            }

            return new AiProviderConnectionTestResult(
                true,
                "Provider test succeeded.",
                $"{settings.DisplayName} responded using {result.ModelId ?? settings.DefaultModelId}.",
                checkedAtUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiProviderConnectionTestResult(
                false,
                "Provider test timed out.",
                settings.ProviderType == AiProviderType.LinuxMadeSaneAiService
                    ? "The host model did not complete the connection check within 130 seconds. Check the Local AI engine on the host."
                    : "The provider did not respond within 20 seconds.",
                checkedAtUtc);
        }
        catch (Exception exception)
        {
            return new AiProviderConnectionTestResult(
                false,
                "Provider test failed.",
                exception.Message,
                checkedAtUtc);
        }
    }

    private async Task<AiProviderConnectionTestResult> TestLinuxMadeSaneAiServiceAsync(
        AiProviderSettings settings,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new InvalidOperationException("The Linux Made Sane AI Service does not have a service URL.");
        }

        var accessKey = string.IsNullOrWhiteSpace(settings.ApiKeySecretReference)
            ? null
            : await secretStore.ResolveSecretAsync(settings.ApiKeySecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException("The Linux Made Sane AI Service does not have an access key.");
        }

        var payload = new JsonObject
        {
            ["model"] = settings.DefaultModelId,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Reply with exactly OK."
                }),
            ["max_tokens"] = 8,
            ["stream"] = false,
            ["think"] = false
        };

        using var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            DeepSeekAiProvider.ResolveOpenAiCompatibleEndpoint(settings.BaseUrl, "chat/completions"))
        {
            Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessKey.Trim());

        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var serviceError = TryReadServiceError(responseBody);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(serviceError)
                ? $"The Linux Made Sane AI Service returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : serviceError);
        }

        var content = JsonNode.Parse(responseBody)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The host model completed the connection check without returning a response.");
        }

        return new AiProviderConnectionTestResult(
            true,
            "Provider test succeeded.",
            $"The host executed a test response using {settings.DefaultModelId}.",
            checkedAtUtc);
    }

    private static string? TryReadServiceError(string responseBody)
    {
        try
        {
            return JsonNode.Parse(responseBody)?["error"] switch
            {
                JsonValue value => value.GetValue<string>(),
                JsonObject error => error["message"]?.GetValue<string>(),
                _ => null
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<AiModelDefinition> BuildModels(AiProviderSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultModelId))
        {
            return Array.Empty<AiModelDefinition>();
        }

        var catalogModel = providerRegistry.ListModelCatalog(settings.ProviderType)
            .FirstOrDefault(model => model.ModelId.Equals(settings.DefaultModelId, StringComparison.OrdinalIgnoreCase));

        return
        [
            new AiModelDefinition(
                settings.ProviderKey,
                settings.DefaultModelId,
                catalogModel?.DisplayName ?? settings.DefaultModelId,
                catalogModel?.Description ?? $"Default model for {settings.DisplayName}",
                null,
                catalogModel?.SupportsToolInvocation ?? settings.ToolUseEnabled)
        ];
    }

    private static AiProviderTurnRequest CreateRequest(AiProviderSettings settings, DateTimeOffset now) =>
        new(
            new AiChatThread(
                Guid.NewGuid(),
                "Provider validation",
                settings.ProviderKey,
                settings.ProviderType,
                settings.DefaultModelId,
                AiTrustProfile.CreatePreset(AiTrustLevel.Guided),
                string.Empty,
                string.Empty,
                now,
                now),
            [],
            [],
            [new AiProviderMessageInputItem(AiChatMessageRole.User, "Reply with exactly OK.")],
            [],
            false,
            false);
}
