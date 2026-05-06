using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class OllamaAiProvider(
    string providerKey,
    AiProviderDefinition definition,
    AiProviderSettings settings,
    IReadOnlyList<AiModelDefinition> models,
    IOllamaRuntimeService runtimeService) : IAiProvider
{
    public string ProviderKey => providerKey;
    public AiProviderDefinition Definition => definition;
    public AiProviderSettings Settings => settings;
    public IReadOnlyList<AiModelDefinition> Models => models;

    public Task<AiProviderTurnResult> ExecuteTurnAsync(
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Thread.ProviderType != AiProviderType.Ollama)
        {
            throw new InvalidOperationException("The local Ollama provider adapter can only execute Ollama chat threads.");
        }

        var modelId = string.IsNullOrWhiteSpace(request.Thread.ModelId)
            ? Settings.DefaultModelId
            : request.Thread.ModelId.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException($"The local provider {Settings.DisplayName} does not have a model selected.");
        }

        return runtimeService.ExecuteAsync(modelId, request, textProgress, cancellationToken);
    }
}
