#pragma warning disable OPENAI001

using System.Text;
using System.Globalization;
using System.ClientModel;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using OpenAI;
using OpenAI.Responses;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class OpenAiAiProvider(
    string providerKey,
    AiProviderDefinition definition,
    AiProviderSettings settings,
    IReadOnlyList<AiModelDefinition> models,
    ISecretStore secretStore) : IAiProvider
{
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
            throw new InvalidOperationException($"The OpenAI provider {Settings.DisplayName} does not have a usable API key.");
        }

        var client = CreateClient(apiKey);
        var options = BuildOptions(request);

        var response = request.StreamingEnabled
            ? await CreateStreamingResponseAsync(client, options, textProgress, cancellationToken)
            : (await client.CreateResponseAsync(options, cancellationToken).ConfigureAwait(false)).Value;

        EnsureResponseSucceeded(response);
        return MapResult(response);
    }

    private ResponsesClient CreateClient(string apiKey)
    {
        var options = new OpenAIClientOptions
        {
            UserAgentApplicationId = "LinuxMadeSane"
        };

        if (!string.IsNullOrWhiteSpace(Settings.BaseUrl))
        {
            options.Endpoint = new Uri(Settings.BaseUrl.Trim(), UriKind.Absolute);
        }

        return new ResponsesClient(new ApiKeyCredential(apiKey), options);
    }

    internal CreateResponseOptions BuildOptions(AiProviderTurnRequest request)
    {
        var internetResearchAllowed = request.InternetResearchAllowed;
        var publishedToolCount = request.AvailableTools.Count + (internetResearchAllowed ? 1 : 0);
        var options = new CreateResponseOptions
        {
            Model = ResolveModelId(request),
            PreviousResponseId = NullIfWhiteSpace(request.Thread.ProviderStateReference),
            StoredOutputEnabled = true,
            StreamingEnabled = request.StreamingEnabled,
            ParallelToolCallsEnabled = true,
            ToolChoice = publishedToolCount == 0
                ? ResponseToolChoice.CreateNoneChoice()
                : ResponseToolChoice.CreateAutoChoice(),
            Instructions = BuildInstructions(request)
        };

        if (!string.IsNullOrWhiteSpace(request.Thread.ProviderConversationReference))
        {
            options.ConversationOptions = new ResponseConversationOptions(request.Thread.ProviderConversationReference);
        }

        foreach (var tool in request.AvailableTools)
        {
            options.Tools.Add(OpenAiResponseToolFactory.Create(tool));
        }

        if (internetResearchAllowed)
        {
            options.Tools.Add(ResponseTool.CreateWebSearchTool(
                BuildWebSearchLocation(),
                WebSearchToolContextSize.Medium,
                null));
        }

        foreach (var inputItem in request.InputItems)
        {
            options.InputItems.Add(MapInputItem(inputItem));
        }

        return options;
    }

    private static ResponseItem MapInputItem(AiProviderInputItem inputItem) => inputItem switch
    {
        AiProviderMessageInputItem message when message.Role == AiChatMessageRole.User =>
            ResponseItem.CreateUserMessageItem(message.Content),
        AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Assistant =>
            ResponseItem.CreateAssistantMessageItem(message.Content),
        AiProviderMessageInputItem message when message.Role == AiChatMessageRole.System =>
            ResponseItem.CreateSystemMessageItem(message.Content),
        AiProviderMessageInputItem message when message.Role == AiChatMessageRole.Tool =>
            ResponseItem.CreateDeveloperMessageItem($"Prior Linux Made Sane tool output:\n{message.Content}"),
        AiProviderToolOutputInputItem toolOutput =>
            ResponseItem.CreateFunctionCallOutputItem(toolOutput.ToolCallId, toolOutput.OutputJson),
        _ => throw new InvalidOperationException($"The provider input type {inputItem.GetType().Name} is not supported.")
    };

    private async Task<ResponseResult> CreateStreamingResponseAsync(
        ResponsesClient client,
        CreateResponseOptions options,
        IProgress<AiProviderTextDelta>? textProgress,
        CancellationToken cancellationToken)
    {
        ResponseResult? completedResponse = null;

        await foreach (var update in client.CreateResponseStreamingAsync(options, cancellationToken))
        {
            switch (update)
            {
                case StreamingResponseOutputTextDeltaUpdate outputTextDelta when !string.IsNullOrEmpty(outputTextDelta.Delta):
                    textProgress?.Report(new AiProviderTextDelta(outputTextDelta.ItemId, outputTextDelta.Delta));
                    break;

                case StreamingResponseErrorUpdate errorUpdate:
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(errorUpdate.Message)
                            ? "OpenAI returned a streaming error without details."
                            : errorUpdate.Message);

                case StreamingResponseFailedUpdate failedUpdate when failedUpdate.Response is not null:
                    throw BuildResponseException(failedUpdate.Response);

                case StreamingResponseCompletedUpdate completedUpdate when completedUpdate.Response is not null:
                    completedResponse = completedUpdate.Response;
                    break;
            }
        }

        return completedResponse
            ?? throw new InvalidOperationException("OpenAI streaming completed without a final response payload.");
    }

    private static AiProviderTurnResult MapResult(ResponseResult response)
    {
        var assistantOutputs = response.OutputItems
            .OfType<MessageResponseItem>()
            .Where(message => message.Role == MessageRole.Assistant)
            .Select(message => new AiProviderAssistantOutput(ExtractAssistantText(message)))
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToArray();

        var toolCalls = response.OutputItems
            .OfType<FunctionCallResponseItem>()
            .Select(functionCall => new AiProviderToolCallRequest(
                functionCall.CallId,
                functionCall.FunctionName,
                functionCall.FunctionArguments.ToString()))
            .ToArray();

        return new AiProviderTurnResult(
            response.Id,
            response.ConversationOptions?.ConversationId,
            response.Model,
            assistantOutputs,
            toolCalls);
    }

    private static string ExtractAssistantText(MessageResponseItem message) =>
        string.Concat(message.Content
            .Where(content => content.Kind == ResponseContentPartKind.OutputText)
            .Select(content => content.Text));

    private static void ValidateRequest(AiProviderTurnRequest request)
    {
        if (request.Thread.ProviderType != AiProviderType.OpenAi)
        {
            throw new InvalidOperationException("The OpenAI provider adapter can only execute OpenAI chat threads.");
        }

        if (request.InputItems.Count == 0)
        {
            throw new InvalidOperationException("At least one provider input item is required.");
        }
    }

    private static void EnsureResponseSucceeded(ResponseResult response)
    {
        if (response.Error is not null)
        {
            throw BuildResponseException(response);
        }

        if (response.Status is ResponseStatus.Failed or ResponseStatus.Incomplete or ResponseStatus.Cancelled)
        {
            throw BuildResponseException(response);
        }
    }

    private static InvalidOperationException BuildResponseException(ResponseResult response)
    {
        var message = response.Error?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            var status = response.Status?.ToString() ?? "Unknown";
            message = $"OpenAI returned a {status} response.";
        }

        return new InvalidOperationException(message);
    }

    private string ResolveModelId(AiProviderTurnRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.Thread.ModelId)
            ? Settings.DefaultModelId
            : request.Thread.ModelId.Trim();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException($"The OpenAI provider {Settings.DisplayName} does not have a model selected.");
        }

        return modelId;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WebSearchToolLocation BuildWebSearchLocation()
    {
        var timezone = TimeZoneInfo.Local.Id;
        var country = string.Empty;

        try
        {
            country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
        }

        return WebSearchToolLocation.CreateApproximateLocation(country, string.Empty, string.Empty, timezone);
    }

    private string BuildInstructions(AiProviderTurnRequest request) =>
        AiProviderInstructionBuilder.Build(request);
}

#pragma warning restore OPENAI001
