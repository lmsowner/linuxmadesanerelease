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

public sealed class DeepSeekAiProvider(
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
            throw new InvalidOperationException($"The DeepSeek provider {Settings.DisplayName} does not have a usable API key.");
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
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

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
            ["messages"] = BuildMessages(request),
            ["max_tokens"] = 4096,
            ["stream"] = false,
            ["thinking"] = new JsonObject
            {
                ["type"] = "disabled"
            }
        };

        var tools = new JsonArray();
        foreach (var tool in request.AvailableTools)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = AiToolJsonSchemaCatalog.ParseParametersSchema(tool)
                }
            });
        }

        if (tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    private JsonArray BuildMessages(AiProviderTurnRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = BuildSystemInstruction(request)
            }
        };

        foreach (var inputItem in request.InputItems)
        {
            switch (inputItem)
            {
                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.System:
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.User:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = message.Content
                    });
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Assistant:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = message.Content
                    });
                    break;

                case AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Tool:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = $"Prior Linux Made Sane tool output:\n{message.Content}"
                    });
                    break;

                case AiProviderToolCallInputItem toolCall:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = null,
                        ["tool_calls"] = new JsonArray(
                            new JsonObject
                            {
                                ["id"] = toolCall.ToolCallId,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = toolCall.ToolName,
                                    ["arguments"] = toolCall.ArgumentsJson
                                }
                            })
                    });
                    break;

                case AiProviderToolOutputInputItem toolOutput:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolOutput.ToolCallId,
                        ["content"] = toolOutput.OutputJson
                    });
                    break;

                default:
                    throw new InvalidOperationException($"The provider input type {inputItem.GetType().Name} is not supported by DeepSeek.");
            }
        }

        return messages;
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

        if (request.InternetResearchAllowed)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Internet research was requested, but this DeepSeek chat adapter does not expose a provider-hosted web-search tool. Use only the provided context and available Linux Made Sane tools.");
        }

        return builder.ToString().Trim();
    }

    private string ResolveModelId(AiProviderTurnRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.Thread.ModelId)
            ? Settings.DefaultModelId
            : request.Thread.ModelId.Trim();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException($"The DeepSeek provider {Settings.DisplayName} does not have a model selected.");
        }

        return modelId;
    }

    private static void ValidateRequest(AiProviderTurnRequest request)
    {
        if (request.Thread.ProviderType != AiProviderType.DeepSeek)
        {
            throw new InvalidOperationException("The DeepSeek provider adapter can only execute DeepSeek chat threads.");
        }

        if (request.InputItems.Count == 0)
        {
            throw new InvalidOperationException("At least one provider input item is required.");
        }
    }

    internal Uri ResolveEndpoint()
    {
        if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
        {
            return new Uri("https://api.deepseek.com/chat/completions", UriKind.Absolute);
        }

        return ResolveOpenAiCompatibleEndpoint(Settings.BaseUrl, "chat/completions");
    }

    internal static Uri ResolveOpenAiCompatibleEndpoint(string baseUrl, string pathSuffix)
    {
        var configured = new Uri(baseUrl.Trim(), UriKind.Absolute);
        var normalizedPath = configured.AbsolutePath.TrimEnd('/');
        var normalizedBase = configured.ToString().TrimEnd('/');

        foreach (var knownTerminalPath in new[] { "chat/completions", "models" })
        {
            if (!normalizedPath.EndsWith($"/{knownTerminalPath}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (knownTerminalPath.Equals(pathSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return configured;
            }

            var root = normalizedBase[..^knownTerminalPath.Length].TrimEnd('/');
            return new Uri($"{root}/{pathSuffix}", UriKind.Absolute);
        }

        return new Uri($"{normalizedBase}/{pathSuffix}", UriKind.Absolute);
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
                ? $"DeepSeek returned {(int)response.StatusCode} {response.ReasonPhrase}."
                : errorMessage);
    }

    internal static AiProviderTurnResult MapResult(string responseBody, AiProviderTurnRequest request)
    {
        var document = JsonNode.Parse(responseBody)?.AsObject()
            ?? throw new InvalidOperationException("DeepSeek returned an invalid JSON response.");

        var choices = document["choices"] as JsonArray ?? [];
        if (choices.Count == 0)
        {
            throw new InvalidOperationException("DeepSeek returned no choices.");
        }

        var choice = choices[0]?.AsObject()
                     ?? throw new InvalidOperationException("DeepSeek returned an invalid choice payload.");
        var message = choice["message"]?.AsObject()
                      ?? throw new InvalidOperationException("DeepSeek returned an invalid message payload.");
        var responseId = document["id"]?.GetValue<string>() ?? $"deepseek-{Guid.NewGuid():N}";
        var modelId = document["model"]?.GetValue<string>() ?? request.Thread.ModelId;

        var text = message["content"]?.GetValue<string>();
        IReadOnlyList<AiProviderAssistantOutput> assistantOutputs = string.IsNullOrWhiteSpace(text)
            ? Array.Empty<AiProviderAssistantOutput>()
            : [new AiProviderAssistantOutput(text.Trim())];

        var toolCalls = new List<AiProviderToolCallRequest>();
        if (message["tool_calls"] is JsonArray responseToolCalls)
        {
            foreach (var toolCallNode in responseToolCalls.OfType<JsonObject>())
            {
                var toolCallId = toolCallNode["id"]?.GetValue<string>();
                var function = toolCallNode["function"] as JsonObject;
                var toolName = function?["name"]?.GetValue<string>();
                var argumentsJson = function?["arguments"]?.GetValue<string>() ?? "{}";

                if (!string.IsNullOrWhiteSpace(toolCallId) &&
                    !string.IsNullOrWhiteSpace(toolName))
                {
                    toolCalls.Add(new AiProviderToolCallRequest(
                        toolCallId.Trim(),
                        toolName.Trim(),
                        string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson));
                }
            }
        }

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
            return document?["error"] switch
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
}
