// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class GeminiAiProvider(
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
            throw new InvalidOperationException($"The Gemini provider {Settings.DisplayName} does not have a usable API key.");
        }

        using var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(request))
        {
            Content = new StringContent(
                BuildRequestPayload(request).ToJsonString(JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Add("x-goog-api-key", apiKey.Trim());

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureResponseSucceeded(response, responseBody);

        return MapResult(responseBody, request);
    }

    internal JsonObject BuildRequestPayload(AiProviderTurnRequest request)
    {
        var payload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject
                    {
                        ["text"] = BuildSystemInstruction(request)
                    })
            },
            ["contents"] = BuildContents(request),
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = 4096
            }
        };

        var tools = new JsonArray();
        if (request.AvailableTools.Count > 0)
        {
            tools.Add(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(
                    request.AvailableTools
                        .Select(tool => new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["parameters"] = NormalizeParametersSchemaForGemini(
                                AiToolJsonSchemaCatalog.ParseParametersSchema(tool))
                        })
                        .ToArray<JsonNode?>())
            });
        }

        if (request.InternetResearchAllowed)
        {
            tools.Add(new JsonObject
            {
                ["googleSearch"] = new JsonObject()
            });
        }

        if (tools.Count > 0)
        {
            payload["tools"] = tools;
        }

        return payload;
    }

    private static JsonArray BuildContents(AiProviderTurnRequest request)
    {
        var contents = new JsonArray();
        JsonObject? currentContent = null;
        string currentRole = string.Empty;

        void AppendPart(string role, JsonNode part)
        {
            if (currentContent is null || !string.Equals(currentRole, role, StringComparison.Ordinal))
            {
                currentContent = new JsonObject
                {
                    ["role"] = role,
                    ["parts"] = new JsonArray()
                };
                contents.Add(currentContent);
                currentRole = role;
            }

            ((JsonArray)currentContent["parts"]!).Add(part);
        }

        foreach (var inputItem in request.InputItems)
        {
            switch (inputItem)
            {
                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Assistant:
                    AppendPart("model", new JsonObject { ["text"] = message.Content });
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.User:
                    AppendPart("user", new JsonObject { ["text"] = message.Content });
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Tool:
                    AppendPart("user", new JsonObject { ["text"] = $"Prior Linux Made Sane tool output:\n{message.Content}" });
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.System:
                    AppendPart("user", new JsonObject { ["text"] = message.Content });
                    break;

                case AiProviderToolCallInputItem toolCall:
                    var functionCall = new JsonObject
                    {
                        ["name"] = toolCall.ToolName,
                        ["args"] = ParseJsonObject(toolCall.ArgumentsJson)
                    };
                    if (!string.IsNullOrWhiteSpace(toolCall.ToolCallId))
                    {
                        functionCall["id"] = toolCall.ToolCallId.Trim();
                    }

                    AppendPart("model", new JsonObject
                    {
                        ["functionCall"] = functionCall
                    });
                    break;

                case AiProviderToolOutputInputItem toolOutput:
                    var functionResponse = new JsonObject
                    {
                        ["name"] = toolOutput.ToolName,
                        ["response"] = BuildFunctionResponse(toolOutput.OutputJson)
                    };
                    if (!string.IsNullOrWhiteSpace(toolOutput.ToolCallId))
                    {
                        functionResponse["id"] = toolOutput.ToolCallId.Trim();
                    }

                    AppendPart("user", new JsonObject
                    {
                        ["functionResponse"] = functionResponse
                    });
                    break;

                default:
                    throw new InvalidOperationException($"The provider input type {inputItem.GetType().Name} is not supported by Gemini.");
            }
        }

        return contents;
    }

    internal static JsonNode NormalizeParametersSchemaForGemini(JsonNode schema)
    {
        var clone = schema.DeepClone();
        NormalizeGeminiSchemaNode(clone);
        return clone;
    }

    private static void NormalizeGeminiSchemaNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var propertyName in obj.Select(item => item.Key).ToArray())
                {
                    if (IsSupportedGeminiSchemaProperty(propertyName))
                    {
                        continue;
                    }

                    obj.Remove(propertyName);
                }

                if (obj["properties"] is JsonObject properties)
                {
                    foreach (var (_, propertySchema) in properties)
                    {
                        if (propertySchema is not null)
                        {
                            NormalizeGeminiSchemaNode(propertySchema);
                        }
                    }

                    if (obj["required"] is JsonArray required)
                    {
                        var validPropertyNames = properties.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
                        var validRequired = new JsonArray();
                        foreach (var requiredItem in required)
                        {
                            var requiredName = requiredItem?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(requiredName) &&
                                validPropertyNames.Contains(requiredName))
                            {
                                validRequired.Add(requiredName);
                            }
                        }

                        if (validRequired.Count == 0)
                        {
                            obj.Remove("required");
                        }
                        else
                        {
                            obj["required"] = validRequired;
                        }
                    }
                }

                if (obj["items"] is JsonNode items)
                {
                    NormalizeGeminiSchemaNode(items);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        NormalizeGeminiSchemaNode(item);
                    }
                }

                break;
        }
    }

    private static bool IsSupportedGeminiSchemaProperty(string propertyName) =>
        propertyName is "type" or "description" or "properties" or "items" or "enum" or "required";

    private static JsonObject BuildFunctionResponse(string outputJson)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(outputJson);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        return new JsonObject
        {
            ["result"] = parsed ?? outputJson
        };
    }

    private string ResolveModelId(AiProviderTurnRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.Thread.ModelId)
            ? Settings.DefaultModelId
            : request.Thread.ModelId.Trim();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException($"The Gemini provider {Settings.DisplayName} does not have a model selected.");
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

    private Uri ResolveEndpoint(AiProviderTurnRequest request)
    {
        var modelId = ResolveModelId(request);

        if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
        {
            return new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{modelId}:generateContent", UriKind.Absolute);
        }

        var configured = new Uri(Settings.BaseUrl.Trim(), UriKind.Absolute);
        return configured.AbsolutePath.Contains(":generateContent", StringComparison.OrdinalIgnoreCase)
            ? configured
            : new Uri(configured, $"/v1beta/models/{modelId}:generateContent");
    }

    private static JsonObject ParseJsonObject(string json)
    {
        var node = JsonNode.Parse(json);
        return node as JsonObject
               ?? throw new InvalidOperationException("Tool arguments must be a JSON object.");
    }

    private static void ValidateRequest(AiProviderTurnRequest request)
    {
        if (request.Thread.ProviderType != AiProviderType.Gemini)
        {
            throw new InvalidOperationException("The Gemini provider adapter can only execute Gemini chat threads.");
        }

        if (request.InputItems.Count == 0)
        {
            throw new InvalidOperationException("At least one provider input item is required.");
        }
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
                ? $"Gemini returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : errorMessage);
    }

    internal static AiProviderTurnResult MapResult(string responseBody, AiProviderTurnRequest request)
    {
        var document = JsonNode.Parse(responseBody)?.AsObject()
            ?? throw new InvalidOperationException("Gemini returned an invalid JSON response.");

        if (document["candidates"] is not JsonArray candidates || candidates.Count == 0)
        {
            var promptBlockReason = document["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(promptBlockReason)
                    ? "Gemini returned no candidates."
                    : $"Gemini blocked the prompt: {promptBlockReason}.");
        }

        var candidate = candidates[0] as JsonObject
                        ?? throw new InvalidOperationException("Gemini returned an invalid candidate payload.");
        var content = candidate["content"]?.AsObject();
        var parts = content?["parts"] as JsonArray ?? [];
        var responseId = document["responseId"]?.GetValue<string>() ?? $"gemini-{Guid.NewGuid():N}";
        var modelId = document["modelVersion"]?.GetValue<string>() ?? request.Thread.ModelId;
        var textFragments = new List<string>();
        var toolCalls = new List<AiProviderToolCallRequest>();

        var toolIndex = 0;
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (part["text"]?.GetValue<string>() is { Length: > 0 } text)
            {
                textFragments.Add(text.Trim());
            }

            if (part["functionCall"] is JsonObject functionCall &&
                functionCall["name"]?.GetValue<string>() is { Length: > 0 } toolName)
            {
                toolIndex++;
                var toolCallId = functionCall["id"]?.GetValue<string>();
                toolCalls.Add(new AiProviderToolCallRequest(
                    string.IsNullOrWhiteSpace(toolCallId)
                        ? $"{responseId}:tool:{toolIndex}"
                        : toolCallId.Trim(),
                    toolName.Trim(),
                    (functionCall["args"] ?? new JsonObject()).ToJsonString(JsonOptions)));
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
