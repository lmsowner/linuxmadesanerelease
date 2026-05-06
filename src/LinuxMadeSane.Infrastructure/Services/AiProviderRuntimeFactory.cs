using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class AiProviderRuntimeFactory
{
    public static IAiProvider Create(
        AiProviderDefinition definition,
        AiProviderSettings settings,
        IReadOnlyList<AiModelDefinition> models,
        ISecretStore secretStore,
        IHttpClientFactory httpClientFactory,
        IOllamaRuntimeService ollamaRuntimeService,
        IRemoteLmsAiEngineGateway remoteGateway) =>
        settings.ProviderType switch
        {
            AiProviderType.OpenAi => new OpenAiAiProvider(settings.ProviderKey, definition, settings, models, secretStore),
            AiProviderType.Anthropic => new AnthropicAiProvider(settings.ProviderKey, definition, settings, models, secretStore, httpClientFactory),
            AiProviderType.Gemini => new GeminiAiProvider(settings.ProviderKey, definition, settings, models, secretStore, httpClientFactory),
            AiProviderType.Ollama => new OllamaAiProvider(settings.ProviderKey, definition, settings, models, ollamaRuntimeService),
            AiProviderType.RemoteLmsAiEngine => new RemoteLmsAiEngineAiProvider(settings.ProviderKey, definition, settings, models, remoteGateway),
            _ => throw new NotSupportedException($"{settings.ProviderType} is not implemented yet in this Linux Made Sane build.")
        };
}
