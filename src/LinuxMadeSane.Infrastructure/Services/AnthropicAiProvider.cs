// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AnthropicAiProvider(
    string providerKey,
    AiProviderDefinition definition,
    AiProviderSettings settings,
    IReadOnlyList<AiModelDefinition> models,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory) : IAiProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ProviderKey => providerKey;
    public AiProviderDefinition Definition => definition;
    public AiProviderSettings Settings => settings;
    public IReadOnlyList<AiModelDefinition> Models => models;

    public async Task<AiProviderTurnResult> ExecuteTurnAsync(
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var apiKey = await secretStore.ResolveSecretAsync(Settings.ApiKeySecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"The Anthropic provider {Settings.DisplayName} does not have a usable API key.");
        }

        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint())
        {
            Content = new StringContent(
                BuildRequestPayload(request).ToJsonString(JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Add("x-api-key", apiKey.Trim());
        message.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, responseBody);

        return MapResult(responseBody, request);
    }

    internal JsonObject BuildRequestPayload(AiProviderTurnRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = ResolveModelId(request),
            ["max_tokens"] = 4096,
            ["system"] = BuildSystemInstruction(request),
            ["messages"] = BuildMessages(request)
        };

        var tools = new JsonArray();
        foreach (var tool in request.AvailableTools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = AiToolJsonSchemaCatalog.ParseParametersSchema(tool)
            });
        }

        if (request.InternetResearchAllowed)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "web_search_20250305",
                ["name"] = "web_search",
                ["max_uses"] = 5
            });
        }

        if (tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = new JsonObject
            {
                ["type"] = "auto"
            };
        }

        return payload;
    }

    private static JsonArray BuildMessages(AiProviderTurnRequest request)
    {
        var messages = new JsonArray();
        var pendingUserBlocks = new JsonArray();
        var pendingAssistantBlocks = new JsonArray();

        void FlushAssistant()
        {
            if (pendingAssistantBlocks.Count == 0)
            {
                return;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = pendingAssistantBlocks.DeepClone()
            });
            pendingAssistantBlocks.Clear();
        }

        void FlushUser()
        {
            if (pendingUserBlocks.Count == 0)
            {
                return;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = pendingUserBlocks.DeepClone()
            });
            pendingUserBlocks.Clear();
        }

        foreach (var inputItem in request.InputItems)
        {
            switch (inputItem)
            {
                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Assistant:
                    FlushUser();
                    pendingAssistantBlocks.Add(CreateTextBlock(message.Content));
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.User:
                    FlushAssistant();
                    pendingUserBlocks.Add(CreateTextBlock(message.Content));
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Tool:
                    FlushAssistant();
                    pendingUserBlocks.Add(CreateTextBlock($"Prior Linux Made Sane tool output:\n{message.Content}"));
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.System:
                    FlushAssistant();
                    pendingUserBlocks.Add(CreateTextBlock(message.Content));
                    break;

                case AiProviderToolCallInputItem toolCall:
                    FlushUser();
                    pendingAssistantBlocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.ToolCallId,
                        ["name"] = toolCall.ToolName,
                        ["input"] = ParseJsonObject(toolCall.ArgumentsJson)
                    });
                    break;

                case AiProviderToolOutputInputItem toolOutput:
                    FlushAssistant();
                    pendingUserBlocks.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolOutput.ToolCallId,
                        ["content"] = toolOutput.OutputJson
                    });
                    break;

                default:
                    throw new InvalidOperationException($"The provider input type {inputItem.GetType().Name} is not supported by Anthropic.");
            }
        }

        FlushAssistant();
        FlushUser();
        return messages;
    }

    private static JsonObject CreateTextBlock(string content) =>
        new()
        {
            ["type"] = "text",
            ["text"] = content
        };

    private string ResolveModelId(AiProviderTurnRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.Thread.ModelId)
            ? Settings.DefaultModelId
            : request.Thread.ModelId.Trim();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException($"The Anthropic provider {Settings.DisplayName} does not have a model selected.");
        }

        return modelId;
    }

    private string BuildSystemInstruction(AiProviderTurnRequest request)
    {
        var builder = new StringBuilder(AiProviderInstructionBuilder.Build(request));

        foreach (var systemMessage in request.InputItems
                     .OfType<AiProviderMessageInputItem>()
                     .Where(message => message.Role == AiChatMessageRole.System))
        {
            if (!string.IsNullOrWhiteSpace(systemMessage.Content))
            {
                builder.AppendLine();
                builder.AppendLine();
                builder.AppendLine(systemMessage.Content.Trim());
            }
        }

        return builder.ToString().Trim();
    }

    private static JsonObject ParseJsonObject(string json)
    {
        var node = JsonNode.Parse(json);
        return node as JsonObject
               ?? throw new InvalidOperationException("Tool arguments must be a JSON object.");
    }

    private static void ValidateRequest(AiProviderTurnRequest request)
    {
        if (request.Thread.ProviderType != AiProviderType.Anthropic)
        {
            throw new InvalidOperationException("The Anthropic provider adapter can only execute Anthropic chat threads.");
        }

        if (request.InputItems.Count == 0)
        {
            throw new InvalidOperationException("At least one provider input item is required.");
        }
    }

    private Uri ResolveEndpoint()
    {
        if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
        {
            return new Uri("https://api.anthropic.com/v1/messages", UriKind.Absolute);
        }

        var configured = new Uri(Settings.BaseUrl.Trim(), UriKind.Absolute);
        return configured.AbsolutePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
            ? configured
            : new Uri(configured, "/v1/messages");
    }

    private static void EnsureResponseSucceeded(HttpResponseMessage response, string responseBody)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorMessage = TryExtractErrorMessage(responseBody);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(errorMessage)
                ? $"Anthropic returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : errorMessage);
    }

    private static AiProviderTurnResult MapResult(string responseBody, AiProviderTurnRequest request)
    {
        var document = JsonNode.Parse(responseBody)?.AsObject()
            ?? throw new InvalidOperationException("Anthropic returned an invalid JSON response.");

        var responseId = document["id"]?.GetValue<string>() ?? $"anthropic-{Guid.NewGuid():N}";
        var modelId = document["model"]?.GetValue<string>() ?? request.Thread.ModelId;
        var content = document["content"] as JsonArray ?? [];

        var textFragments = new List<string>();
        var toolCalls = new List<AiProviderToolCallRequest>();

        foreach (var block in content.OfType<JsonObject>())
        {
            var type = block["type"]?.GetValue<string>() ?? string.Empty;
            switch (type)
            {
                case "text":
                    var text = block["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textFragments.Add(text.Trim());
                    }

                    break;

                case "tool_use":
                    var toolCallId = block["id"]?.GetValue<string>();
                    var toolName = block["name"]?.GetValue<string>();
                    var input = block["input"];
                    if (!string.IsNullOrWhiteSpace(toolCallId) &&
                        !string.IsNullOrWhiteSpace(toolName) &&
                        input is not null)
                    {
                        toolCalls.Add(new AiProviderToolCallRequest(
                            toolCallId.Trim(),
                            toolName.Trim(),
                            input.ToJsonString(JsonOptions)));
                    }

                    break;
            }
        }

        var assistantOutputs = string.Join(Environment.NewLine + Environment.NewLine, textFragments)
            is { Length: > 0 } mergedText
            ? new[] { new AiProviderAssistantOutput(mergedText) }
            : Array.Empty<AiProviderAssistantOutput>();

        return new AiProviderTurnResult(
            responseId,
            null,
            modelId,
            assistantOutputs,
            toolCalls);
    }

    private static string? TryExtractErrorMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            var document = JsonNode.Parse(responseBody)?.AsObject();
            return document?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
