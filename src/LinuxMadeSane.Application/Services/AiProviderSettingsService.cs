using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Services;

public sealed class AiProviderSettingsService(
    IAiProviderSettingsStore providerSettingsStore,
    IAiProviderRegistry providerRegistry,
    ISecretStore secretStore,
    IAiProviderConnectionTester connectionTester) : IAiProviderSettingsService
{
    public async Task<AiProviderSettingsPageViewModel> GetPageAsync(CancellationToken cancellationToken = default)
    {
        var providers = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var supportedProviders = providerRegistry.ListSupportedProviders();

        return new AiProviderSettingsPageViewModel(
            supportedProviders,
            AiProviderViewModelMapper.Map(
                providers,
                provider => supportedProviders.FirstOrDefault(item => item.ProviderType == provider.ProviderType)?.RequiresApiKey != false));
    }

    public async Task<AiProviderSettingsEditorContextViewModel> GetEditorAsync(
        string? providerKey = null,
        CancellationToken cancellationToken = default)
    {
        var existingProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var supportedProviders = providerRegistry.ListSupportedProviders();
        var modelCatalog = providerRegistry.ListModelCatalog();
        var mappedProviders = AiProviderViewModelMapper.Map(
            existingProviders,
            provider => supportedProviders.FirstOrDefault(item => item.ProviderType == provider.ProviderType)?.RequiresApiKey != false);

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            var editor = BuildDefaultEditor(existingProviders, modelCatalog);
            return new AiProviderSettingsEditorContextViewModel(
                editor,
                supportedProviders,
                EnsureCurrentModelIsListed(editor, modelCatalog),
                mappedProviders,
                true);
        }

        var provider = await providerSettingsStore.GetAsync(providerKey.Trim(), cancellationToken);
        if (provider is null)
        {
            var editor = BuildDefaultEditor(existingProviders, modelCatalog);
            return new AiProviderSettingsEditorContextViewModel(
                editor,
                supportedProviders,
                EnsureCurrentModelIsListed(editor, modelCatalog),
                mappedProviders,
                false);
        }

        if (supportedProviders.All(item => item.ProviderType != provider.ProviderType))
        {
            var editor = BuildDefaultEditor(existingProviders, modelCatalog);
            return new AiProviderSettingsEditorContextViewModel(
                editor,
                supportedProviders,
                EnsureCurrentModelIsListed(editor, modelCatalog),
                mappedProviders,
                false);
        }

        var existingEditor = MapEditor(provider);
        existingEditor.RequiresApiKey = supportedProviders.FirstOrDefault(item => item.ProviderType == existingEditor.ProviderType)?.RequiresApiKey != false;
        return new AiProviderSettingsEditorContextViewModel(
            existingEditor,
            supportedProviders,
            EnsureCurrentModelIsListed(existingEditor, modelCatalog),
            mappedProviders,
            true);
    }

    public async Task<string> SaveAsync(AiProviderSettingsEditor editor, CancellationToken cancellationToken = default)
    {
        ValidateEditor(editor);

        var supportedProviders = providerRegistry.ListSupportedProviders();
        if (supportedProviders.All(provider => provider.ProviderType != editor.ProviderType))
        {
            throw new InvalidOperationException("The selected provider type is not supported.");
        }

        var allProviders = await providerSettingsStore.ListAsync(cancellationToken);
        var existing = string.IsNullOrWhiteSpace(editor.ProviderKey)
            ? null
            : allProviders.FirstOrDefault(provider => provider.ProviderKey.Equals(editor.ProviderKey, StringComparison.OrdinalIgnoreCase));

        if (existing is null && !string.IsNullOrWhiteSpace(editor.ProviderKey))
        {
            throw new InvalidOperationException("That provider record no longer exists.");
        }

        var supportedModels = providerRegistry.ListModelCatalog(editor.ProviderType);
        var selectedModelId = editor.DefaultModelId.Trim();
        var selectedModelIsSupported = supportedModels.Any(model => model.ModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase));
        if (!selectedModelIsSupported &&
            (existing is null || !existing.DefaultModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Select a supported default model for the selected provider.");
        }

        var now = DateTimeOffset.UtcNow;
        var providerKey = existing?.ProviderKey ?? GenerateProviderKey(editor, allProviders);
        var newSecretReference = string.Empty;
        var secretReference = existing?.ApiKeySecretReference ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(editor.ApiKeyInput))
        {
            newSecretReference = await secretStore.StoreSecretAsync(
                editor.ApiKeyInput.Trim(),
                $"ai-provider:{providerKey}",
                cancellationToken);

            secretReference = newSecretReference;
        }
        else if (editor.ClearStoredApiKey)
        {
            secretReference = string.Empty;
        }

        var shouldBeDefault = editor.IsDefault
            || (existing is null && allProviders.Count == 0 && editor.IsEnabled);

        var settings = new AiProviderSettings(
            providerKey,
            editor.ProviderType,
            editor.DisplayName.Trim(),
            editor.IsEnabled,
            shouldBeDefault,
            string.Empty,
            editor.DefaultModelId.Trim(),
            editor.StreamingEnabled,
            editor.ToolUseEnabled,
            existing?.Notes ?? string.Empty,
            existing?.MetadataJson ?? string.Empty,
            secretReference,
            existing?.CreatedAtUtc ?? now,
            now);

        await providerSettingsStore.SaveAsync(settings, cancellationToken);

        if (shouldBeDefault)
        {
            foreach (var provider in allProviders.Where(provider =>
                         !provider.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase) &&
                         provider.IsDefault))
            {
                await providerSettingsStore.SaveAsync(provider with
                {
                    IsDefault = false,
                    UpdatedAtUtc = now
                }, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(newSecretReference) &&
            !string.IsNullOrWhiteSpace(existing?.ApiKeySecretReference) &&
            !existing.ApiKeySecretReference.Equals(newSecretReference, StringComparison.Ordinal))
        {
            await secretStore.DeleteSecretAsync(existing.ApiKeySecretReference, cancellationToken);
        }

        if (editor.ClearStoredApiKey && !string.IsNullOrWhiteSpace(existing?.ApiKeySecretReference))
        {
            await secretStore.DeleteSecretAsync(existing.ApiKeySecretReference, cancellationToken);
        }

        return providerKey;
    }

    public async Task<AiProviderConnectionTestResult> TestAsync(
        AiProviderSettingsEditor editor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        if (editor.ProviderType == AiProviderType.Unknown)
        {
            throw new InvalidOperationException("Select a supported provider type before testing.");
        }

        if (string.IsNullOrWhiteSpace(editor.DefaultModelId))
        {
            throw new InvalidOperationException("Select a default model before testing.");
        }

        var supportedProviders = providerRegistry.ListSupportedProviders();
        var definition = supportedProviders.FirstOrDefault(provider => provider.ProviderType == editor.ProviderType);
        if (definition is null)
        {
            throw new InvalidOperationException("The selected provider type is not supported.");
        }

        var allProviders = await providerSettingsStore.ListAsync(cancellationToken);
        var existing = string.IsNullOrWhiteSpace(editor.ProviderKey)
            ? null
            : allProviders.FirstOrDefault(provider => provider.ProviderKey.Equals(editor.ProviderKey, StringComparison.OrdinalIgnoreCase));

        if (existing is null && !string.IsNullOrWhiteSpace(editor.ProviderKey))
        {
            throw new InvalidOperationException("That provider record no longer exists.");
        }

        var supportedModels = providerRegistry.ListModelCatalog(editor.ProviderType);
        var selectedModelId = editor.DefaultModelId.Trim();
        var selectedModelIsSupported = supportedModels.Any(model => model.ModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase));
        if (!selectedModelIsSupported &&
            (existing is null || !existing.DefaultModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Select a supported default model before testing.");
        }

        var effectiveSecretReference = existing?.ApiKeySecretReference ?? string.Empty;
        string? temporarySecretReference = null;

        if (!string.IsNullOrWhiteSpace(editor.ApiKeyInput))
        {
            temporarySecretReference = await secretStore.StoreSecretAsync(
                editor.ApiKeyInput.Trim(),
                $"ai-provider-test:{existing?.ProviderKey ?? "unsaved"}",
                cancellationToken);

            effectiveSecretReference = temporarySecretReference;
        }
        else if (editor.ClearStoredApiKey)
        {
            effectiveSecretReference = string.Empty;
        }

        if (definition.RequiresApiKey && string.IsNullOrWhiteSpace(effectiveSecretReference))
        {
            throw new InvalidOperationException("Enter an API key or keep the stored key before testing.");
        }

        var now = DateTimeOffset.UtcNow;
        var displayName = string.IsNullOrWhiteSpace(editor.DisplayName)
            ? definition.DisplayName
            : editor.DisplayName.Trim();
        var transientProviderKey = existing?.ProviderKey ?? GenerateProviderKey(
            new AiProviderSettingsEditor
            {
                ProviderType = editor.ProviderType,
                DisplayName = displayName
            },
            allProviders);

        var settings = new AiProviderSettings(
            transientProviderKey,
            editor.ProviderType,
            displayName,
            editor.IsEnabled,
            editor.IsDefault,
            existing?.BaseUrl ?? string.Empty,
            selectedModelId,
            editor.StreamingEnabled,
            editor.ToolUseEnabled,
            existing?.Notes ?? string.Empty,
            existing?.MetadataJson ?? string.Empty,
            effectiveSecretReference,
            existing?.CreatedAtUtc ?? now,
            now);

        try
        {
            return await connectionTester.TestAsync(settings, cancellationToken);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporarySecretReference))
            {
                await secretStore.DeleteSecretAsync(temporarySecretReference, cancellationToken);
            }
        }
    }

    private AiProviderSettingsEditor BuildDefaultEditor(
        IReadOnlyList<AiProviderSettings> providers,
        IReadOnlyList<AiProviderModelOption> modelCatalog)
    {
        const AiProviderType defaultProviderType = AiProviderType.OpenAi;
        var defaultDefinition = providerRegistry.FindDefinition(defaultProviderType);
        var defaultModelId = modelCatalog
            .Where(model => model.ProviderType == defaultProviderType)
            .OrderByDescending(model => model.IsRecommendedDefault)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.ModelId)
            .FirstOrDefault()
            ?? string.Empty;

        return new AiProviderSettingsEditor
        {
            ProviderType = defaultProviderType,
            DisplayName = "OpenAI",
            StreamingEnabled = true,
            ToolUseEnabled = true,
            RequiresApiKey = defaultDefinition?.RequiresApiKey != false,
            IsEnabled = true,
            IsDefault = providers.Count == 0,
            DefaultModelId = defaultModelId
        };
    }

    private static AiProviderSettingsEditor MapEditor(AiProviderSettings provider) =>
        new()
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.ProviderType,
            DisplayName = provider.DisplayName,
            IsEnabled = provider.IsEnabled,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.DefaultModelId,
            StreamingEnabled = provider.StreamingEnabled,
            ToolUseEnabled = provider.ToolUseEnabled,
            RequiresApiKey = true,
            HasApiKeyConfigured = !string.IsNullOrWhiteSpace(provider.ApiKeySecretReference)
        };

    private static IReadOnlyList<AiProviderModelOption> EnsureCurrentModelIsListed(
        AiProviderSettingsEditor editor,
        IReadOnlyList<AiProviderModelOption> modelCatalog)
    {
        if (string.IsNullOrWhiteSpace(editor.DefaultModelId) ||
            modelCatalog.Any(model =>
                model.ProviderType == editor.ProviderType &&
                model.ModelId.Equals(editor.DefaultModelId, StringComparison.OrdinalIgnoreCase)))
        {
            return modelCatalog;
        }

        return modelCatalog
            .Concat(
            [
                new AiProviderModelOption(
                    editor.ProviderType,
                    editor.DefaultModelId,
                    editor.DefaultModelId,
                    "Existing configured model.",
                    true,
                    false)
            ])
            .ToArray();
    }

    private static string GenerateProviderKey(
        AiProviderSettingsEditor editor,
        IReadOnlyList<AiProviderSettings> existingProviders)
    {
        var providerSegment = editor.ProviderType switch
        {
            AiProviderType.OpenAi => "openai",
            AiProviderType.Anthropic => "anthropic",
            AiProviderType.Ollama => "local-ollama",
            AiProviderType.RemoteLmsAiEngine => "remote-ai-engine",
            AiProviderType.Gemini => "gemini",
            _ => "provider"
        };

        var nameSegment = Slugify(editor.DisplayName);
        var baseKey = string.IsNullOrWhiteSpace(nameSegment)
            ? providerSegment
            : $"{providerSegment}-{nameSegment}";
        var candidate = baseKey;
        var suffix = 2;

        while (existingProviders.Any(provider => provider.ProviderKey.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseKey}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        var builder = new List<char>(value.Length);
        var previousWasSeparator = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Add(character);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
            {
                continue;
            }

            builder.Add('-');
            previousWasSeparator = true;
        }

        return new string(builder.ToArray()).Trim('-');
    }

    private static void ValidateEditor(AiProviderSettingsEditor editor)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(editor);
        var isValid = Validator.TryValidateObject(editor, context, results, true);

        if (isValid)
        {
            return;
        }

        throw new InvalidOperationException(string.Join(" ", results.Select(result => result.ErrorMessage)));
    }
}
