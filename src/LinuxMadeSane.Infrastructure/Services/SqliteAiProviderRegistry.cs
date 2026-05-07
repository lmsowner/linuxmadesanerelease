using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteAiProviderRegistry(
    IAiProviderSettingsStore providerSettingsStore,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    ILocalModelManagementService localModelManagementService,
    IOllamaRuntimeService ollamaRuntimeService,
    IRemoteLmsAiEngineGateway remoteGateway,
    ILmsConnectClientFeature connectClientFeature) : IAiProviderRegistry
{
    private static readonly IReadOnlyList<AiProviderDefinition> CommonProviderDefinitions =
    [
        new("openai", AiProviderType.OpenAi, "OpenAI", "Runnable in this build.", true, true),
        new("anthropic", AiProviderType.Anthropic, "Anthropic", "Runnable in this build.", true, true, true, string.Empty, false),
        new("gemini", AiProviderType.Gemini, "Gemini", "Runnable in this build.", true, true, true, string.Empty, false),
        new("ollama", AiProviderType.Ollama, "Local Ollama", "Linux Made Sane local AI engine powered by Ollama.", true, true, true, string.Empty, false, false)
    ];
    private static readonly AiProviderDefinition RemoteProviderDefinition =
        new("remote-lms-ai-engine", AiProviderType.RemoteLmsAiEngine, "Remote LMS AI Engine", "Secure AI inference routed through another Linux Made Sane instance by way of LMS Connect.", true, true, true, string.Empty, false, false);
    private static readonly IReadOnlyList<AiProviderModelOption> CloudModelCatalog =
    [
        new(AiProviderType.OpenAi, "gpt-5.5", "GPT-5.5", "Latest OpenAI flagship model for complex reasoning, coding, and professional work.", true, true),
        new(AiProviderType.OpenAi, "gpt-5.5-pro", "GPT-5.5 Pro", "Highest-capability GPT-5.5 variant for demanding reasoning work.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.4", "GPT-5.4", "Current lower-cost OpenAI frontier model for complex reasoning and coding.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.4-pro", "GPT-5.4 Pro", "Higher-compute GPT-5.4 variant for difficult reasoning and coding tasks.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.4-mini", "GPT-5.4 mini", "Lower-latency GPT-5.4 variant for coding, computer use, and subagents.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.4-nano", "GPT-5.4 nano", "Lowest-cost GPT-5.4-class model for lightweight high-volume tasks.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.2", "GPT-5.2", "Previous GPT-5 generation model for reasoning and coding.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.2-chat-latest", "ChatGPT 5.2", "GPT-5.2 snapshot currently exposed through the chat alias.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.2-codex", "GPT-5.2 Codex", "GPT-5.2 variant optimized for agentic coding workflows.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.2-pro", "GPT-5.2 Pro", "Highest-cost GPT-5.2 reasoning variant for maximum quality.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.1", "GPT-5.1", "Previous flagship OpenAI model for coding and agentic work.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.1-chat-latest", "ChatGPT 5.1", "GPT-5.1 snapshot currently used in ChatGPT.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.1-codex", "GPT-5.1 Codex", "GPT-5.1 variant optimized for agentic coding tasks.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.1-codex-max", "GPT-5.1 Codex Max", "Higher-capability GPT-5.1 Codex variant for demanding coding tasks.", true, false),
        new(AiProviderType.OpenAi, "gpt-5.1-codex-mini", "GPT-5.1 Codex mini", "Lower-cost GPT-5.1 Codex variant for coding-heavy operational tasks.", true, false),
        new(AiProviderType.OpenAi, "gpt-5", "GPT-5", "Earlier GPT-5 reasoning model with configurable reasoning effort.", true, false),
        new(AiProviderType.OpenAi, "gpt-5-chat-latest", "ChatGPT 5", "GPT-5 snapshot exposed through the chat alias.", true, false),
        new(AiProviderType.OpenAi, "gpt-5-codex", "GPT-5 Codex", "GPT-5 variant optimized for agentic coding in Codex-style environments.", true, false),
        new(AiProviderType.OpenAi, "gpt-5-pro", "GPT-5 Pro", "Highest-cost GPT-5 reasoning variant for maximum quality.", true, false),
        new(AiProviderType.OpenAi, "gpt-5-mini", "GPT-5 mini", "Lower-cost OpenAI model for well-defined operational tasks.", true, false),
        new(AiProviderType.OpenAi, "gpt-5-nano", "GPT-5 nano", "Fastest low-cost OpenAI model for lightweight summaries and classification.", true, false),
        new(AiProviderType.OpenAi, "codex-mini-latest", "Codex mini latest", "Dedicated Codex mini model for coding-oriented environments.", true, false),
        new(AiProviderType.OpenAi, "gpt-4.1", "GPT-4.1", "High-capability non-reasoning OpenAI model with strong tool support.", true, false),
        new(AiProviderType.OpenAi, "gpt-4.1-mini", "GPT-4.1 mini", "Smaller GPT-4.1 model for lower-latency non-reasoning work.", true, false),
        new(AiProviderType.OpenAi, "gpt-4.1-nano", "GPT-4.1 nano", "Fastest GPT-4.1 model for lightweight classification and extraction.", true, false),

        new(AiProviderType.Anthropic, "claude-sonnet-4-6", "Claude Sonnet 4.6", "Latest balanced Claude model with strong coding, speed, and long-context reasoning.", true, true),
        new(AiProviderType.Anthropic, "claude-opus-4-7", "Claude Opus 4.7", "Anthropic's most capable generally available Claude model for complex reasoning and agentic coding.", true, false),
        new(AiProviderType.Anthropic, "claude-haiku-4-5", "Claude Haiku 4.5", "Fast Claude model with near-frontier intelligence for lighter workloads.", true, false),
        new(AiProviderType.Anthropic, "claude-haiku-4-5-20251001", "Claude Haiku 4.5 snapshot", "Pinned Claude Haiku 4.5 snapshot.", true, false),
        new(AiProviderType.Anthropic, "claude-sonnet-4-20250514", "Claude Sonnet 4", "Earlier Claude 4 model with strong reasoning and coding performance.", true, false),
        new(AiProviderType.Anthropic, "claude-opus-4-1-20250805", "Claude Opus 4.1", "Highest-capability Claude 4.1 model.", true, false),
        new(AiProviderType.Anthropic, "claude-3-7-sonnet-20250219", "Claude Sonnet 3.7", "Earlier high-capability Claude model with extended thinking support.", true, false),
        new(AiProviderType.Anthropic, "claude-3-5-haiku-20241022", "Claude Haiku 3.5", "Fast lower-cost Claude model for lighter workloads.", true, false),

        new(AiProviderType.Gemini, "gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "Latest stable low-latency Gemini model for high-frequency lightweight tasks.", true, true),
        new(AiProviderType.Gemini, "gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", "Latest Gemini 3.1 Pro preview for advanced reasoning, coding, and agentic workflows.", true, false),
        new(AiProviderType.Gemini, "gemini-3-flash-preview", "Gemini 3 Flash Preview", "Gemini 3 Flash preview for frontier-class performance at lower latency and cost.", true, false),
        new(AiProviderType.Gemini, "gemini-3.1-flash-lite-preview", "Gemini 3.1 Flash-Lite Preview", "Preview Gemini 3.1 Flash-Lite endpoint for fast lightweight work.", true, false),
        new(AiProviderType.Gemini, "gemini-2.5-pro", "Gemini 2.5 Pro", "Advanced Gemini model for complex reasoning and coding tasks.", true, false),
        new(AiProviderType.Gemini, "gemini-2.5-flash", "Gemini 2.5 Flash", "Balanced Gemini model for faster production traffic.", true, false),
        new(AiProviderType.Gemini, "gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", "Fastest low-cost Gemini model for high-throughput workloads.", true, false)
    ];

    public IReadOnlyList<AiProviderDefinition> ListSupportedProviders() =>
        connectClientFeature.SupportsRemoteAiSharing
            ? CommonProviderDefinitions.Concat([RemoteProviderDefinition]).ToArray()
            : CommonProviderDefinitions;

    public IReadOnlyList<AiProviderModelOption> ListModelCatalog(AiProviderType? providerType = null) =>
        BuildModelCatalog()
            .Where(model => providerType is null || model.ProviderType == providerType)
            .OrderBy(model => model.ProviderType)
            .ThenByDescending(model => model.IsRecommendedDefault)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<IReadOnlyList<AiProviderSettings>> ListConfiguredProvidersAsync(CancellationToken cancellationToken = default) =>
        FilterConfiguredProviders(await providerSettingsStore.ListAsync(cancellationToken));

    public async Task<IReadOnlyList<AiModelDefinition>> ListModelsAsync(
        string? providerKey = null,
        CancellationToken cancellationToken = default)
    {
        var providers = FilterConfiguredProviders(await providerSettingsStore.ListAsync(cancellationToken));

        return providers
            .Where(provider =>
                !string.IsNullOrWhiteSpace(provider.DefaultModelId) &&
                (string.IsNullOrWhiteSpace(providerKey) ||
                 provider.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase)))
            .Select(MapModel)
            .OrderBy(model => model.ProviderKey)
            .ThenBy(model => model.DisplayName)
            .ToArray();
    }

    public AiProviderDefinition? FindDefinition(AiProviderType providerType) =>
        ListSupportedProviders().FirstOrDefault(definition => definition.ProviderType == providerType);

    public async Task<IAiProvider?> GetProviderAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var settings = await providerSettingsStore.GetAsync(providerKey, cancellationToken);
        if (settings is null)
        {
            return null;
        }

        if (settings.ProviderType == AiProviderType.RemoteLmsAiEngine &&
            !connectClientFeature.SupportsRemoteAiSharing)
        {
            return new UnavailableAiProvider(
                settings.ProviderKey,
                RemoteProviderDefinition,
                settings,
                Array.Empty<AiModelDefinition>(),
                "The LMS Connect client plugin is not installed in this build.");
        }

        var definition = FindDefinition(settings.ProviderType) ?? new AiProviderDefinition(
            settings.ProviderKey,
            settings.ProviderType,
            settings.DisplayName,
            "Configured provider instance.",
            settings.ToolUseEnabled,
            true);

        IReadOnlyList<AiModelDefinition> models = string.IsNullOrWhiteSpace(settings.DefaultModelId)
            ? Array.Empty<AiModelDefinition>()
            : [MapModel(settings)];

        if (!settings.IsEnabled)
        {
            return new UnavailableAiProvider(
                settings.ProviderKey,
                definition,
                settings,
                models,
                $"The provider {settings.DisplayName} is disabled.");
        }

        return settings.ProviderType switch
        {
            AiProviderType.OpenAi or AiProviderType.Anthropic or AiProviderType.Gemini or AiProviderType.Ollama or AiProviderType.RemoteLmsAiEngine =>
                AiProviderRuntimeFactory.Create(definition, settings, models, secretStore, httpClientFactory, ollamaRuntimeService, remoteGateway),
            _ => new UnavailableAiProvider(
                settings.ProviderKey,
                definition,
                settings,
                models,
                $"{settings.ProviderType} is not implemented yet in this Linux Made Sane build.")
        };
    }

    private static AiModelDefinition MapModel(AiProviderSettings settings)
    {
        var catalogModel = ResolveCatalogModel(settings);

        return new AiModelDefinition(
            settings.ProviderKey,
            settings.DefaultModelId,
            catalogModel?.DisplayName ?? settings.DefaultModelId,
            catalogModel?.Description ?? $"Default model for {settings.DisplayName}",
            null,
            catalogModel?.SupportsToolInvocation ?? settings.ToolUseEnabled);
    }

    private static AiProviderModelOption? ResolveCatalogModel(AiProviderSettings settings) =>
        BuildStaticModelCatalog().FirstOrDefault(model =>
            model.ProviderType == settings.ProviderType &&
            model.ModelId.Equals(settings.DefaultModelId, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<AiProviderModelOption> BuildModelCatalog() =>
        BuildStaticModelCatalog()
            .Concat(localModelManagementService.ListDefinitions().Select(definition =>
                new AiProviderModelOption(
                    AiProviderType.Ollama,
                    definition.ModelId,
                    definition.DisplayName,
                    definition.Description,
                    definition.SupportsTools,
                    definition.IsDefaultRecommendation)))
            .Concat(connectClientFeature.SupportsRemoteAiSharing
                ? localModelManagementService.ListDefinitions().Select(definition =>
                    new AiProviderModelOption(
                        AiProviderType.RemoteLmsAiEngine,
                        definition.ModelId,
                        definition.DisplayName,
                        definition.Description,
                        definition.SupportsTools,
                        definition.IsDefaultRecommendation))
                : [])
            .ToArray();

    private static IReadOnlyList<AiProviderModelOption> BuildStaticModelCatalog() => CloudModelCatalog;

    private IReadOnlyList<AiProviderSettings> FilterConfiguredProviders(IReadOnlyList<AiProviderSettings> providers) =>
        connectClientFeature.SupportsRemoteAiSharing
            ? providers
            : providers.Where(provider => provider.ProviderType != AiProviderType.RemoteLmsAiEngine).ToArray();

    private sealed class UnavailableAiProvider(
        string providerKey,
        AiProviderDefinition definition,
        AiProviderSettings settings,
        IReadOnlyList<AiModelDefinition> models,
        string reason) : IAiProvider
    {
        public string ProviderKey => providerKey;
        public AiProviderDefinition Definition => definition;
        public AiProviderSettings Settings => settings;
        public IReadOnlyList<AiModelDefinition> Models => models;

        public Task<AiProviderTurnResult> ExecuteTurnAsync(
            AiProviderTurnRequest request,
            IProgress<AiProviderTextDelta>? textProgress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(reason);
    }
}
