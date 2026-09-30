// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace LinuxMadeSane.Application.Services;

public sealed class AiProviderSettingsService(
    IAiProviderSettingsStore providerSettingsStore,
    IAiProviderRegistry providerRegistry,
    ISecretStore secretStore,
    IAiProviderConnectionTester connectionTester,
    IAiProviderModelDiscoveryService modelDiscoveryService) : IAiProviderSettingsService
{
    // Circuit-scoped evidence: never trust client-supplied validation or retain API key plaintext.
    private static readonly TimeSpan ValidationLifetime = TimeSpan.FromMinutes(10);
    private ModelCatalogSnapshot? liveCatalog;
    private ModelTestSnapshot? successfulTest;

    private sealed record ModelValidationContext(AiProviderType Type, string ProviderKey, string BaseUrl,
        string SecretReference, string ReplacementKeyHash, bool ClearKey, bool Streaming, bool ToolUse);
    private sealed record ModelCatalogSnapshot(ModelValidationContext Context,
        IReadOnlyList<AiProviderModelOption> Models, DateTimeOffset ExpiresAt);
    private sealed record ModelTestSnapshot(ModelValidationContext Context, string ModelId, DateTimeOffset ExpiresAt);

    private static ModelValidationContext GetValidationContext(AiProviderSettingsEditor editor, AiProviderSettings? existing) => new(
        editor.ProviderType, existing?.ProviderKey ?? string.Empty, ResolveBaseUrl(editor, existing),
        existing?.ApiKeySecretReference ?? string.Empty,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(editor.ApiKeyInput?.Trim() ?? string.Empty))),
        editor.ClearStoredApiKey, editor.StreamingEnabled, editor.ToolUseEnabled);

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

    public async Task<IReadOnlyList<AiProviderModelOption>> RefreshModelCatalogAsync(
        AiProviderSettingsEditor editor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

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

        if (existing is not null && editor.ProviderType != existing.ProviderType)
        {
            throw new InvalidOperationException("Provider type cannot be changed for an existing record. Create a new provider for a different API.");
        }

        if (definition.RequiresApiKey &&
            string.IsNullOrWhiteSpace(editor.ApiKeyInput) &&
            (existing is null || string.IsNullOrWhiteSpace(existing.ApiKeySecretReference) || editor.ClearStoredApiKey))
        {
            throw new InvalidOperationException("Enter an API key or keep the stored key before refreshing models.");
        }

        var now = DateTimeOffset.UtcNow;
        var providerKey = existing?.ProviderKey ?? GenerateProviderKey(editor, allProviders);
        var displayName = string.IsNullOrWhiteSpace(editor.DisplayName)
            ? definition.DisplayName
            : editor.DisplayName.Trim();
        var settings = new AiProviderSettings(
            providerKey,
            editor.ProviderType,
            displayName,
            editor.IsEnabled,
            editor.IsDefault,
            ResolveBaseUrl(editor, existing),
            editor.DefaultModelId.Trim(),
            editor.StreamingEnabled,
            editor.ToolUseEnabled,
            existing?.Notes ?? string.Empty,
            existing?.MetadataJson ?? string.Empty,
            editor.ClearStoredApiKey ? string.Empty : existing?.ApiKeySecretReference ?? string.Empty,
            existing?.CreatedAtUtc ?? now,
            now);

        var context = GetValidationContext(editor, existing);
        var discoveredModels = await modelDiscoveryService.DiscoverAsync(settings, editor.ApiKeyInput, cancellationToken);
        var selectableModels = discoveredModels
            .Where(model => model.ProviderType == editor.ProviderType && !IsAuxiliaryModelArtifact(model.ModelId))
            .DistinctBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        liveCatalog = new(context, selectableModels, DateTimeOffset.UtcNow + ValidationLifetime);
        // A refresh is authoritative. Do not re-add retired bundled or configured models.
        successfulTest = null;
        return providerRegistry.ListModelCatalog()
            .Where(model => model.ProviderType != editor.ProviderType)
            .Concat(selectableModels)
            .ToArray();
    }

    public async Task<string> SaveAsync(AiProviderSettingsEditor editor, CancellationToken cancellationToken = default)
    {
        NormalizeLinuxMadeSaneAiServiceEditor(editor);
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

        if (existing is not null && editor.ProviderType != existing.ProviderType)
        {
            throw new InvalidOperationException("Provider type cannot be changed for an existing record. Create a new provider for a different API.");
        }

        await DiscoverLinuxMadeSaneAiServiceModelAsync(editor, existing, allProviders, cancellationToken);

        var supportedModels = providerRegistry.ListModelCatalog(editor.ProviderType);
        var selectedModelId = editor.DefaultModelId.Trim();
        var selectedModelIsSupported = editor.ProviderType == AiProviderType.LinuxMadeSaneAiService ||
            await IsSelectedModelSupportedAsync(
                editor,
                existing,
                supportedModels,
                selectedModelId,
                allProviders,
                cancellationToken);
        if (!selectedModelIsSupported)
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
            ResolveBaseUrl(editor, existing),
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
        NormalizeLinuxMadeSaneAiServiceEditor(editor);

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

        if (existing is not null && editor.ProviderType != existing.ProviderType)
        {
            throw new InvalidOperationException("Provider type cannot be changed for an existing record. Create a new provider for a different API.");
        }

        await DiscoverLinuxMadeSaneAiServiceModelAsync(editor, existing, allProviders, cancellationToken);

        var selectedModelId = editor.DefaultModelId.Trim();
        if (IsAuxiliaryModelArtifact(selectedModelId))
        {
            throw new InvalidOperationException("Select a chat model before testing.");
        }
        var validationContext = GetValidationContext(editor, existing);
        successfulTest = null;

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
            ResolveBaseUrl(editor, existing),
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
            var result = await connectionTester.TestAsync(settings, cancellationToken);
            if (result.Succeeded)
            {
                successfulTest = new(validationContext, selectedModelId, DateTimeOffset.UtcNow + ValidationLifetime);
            }
            return result;
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
            BaseUrl = provider.BaseUrl,
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
            IsAuxiliaryModelArtifact(editor.DefaultModelId) ||
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

    private async Task<bool> IsSelectedModelSupportedAsync(
        AiProviderSettingsEditor editor,
        AiProviderSettings? existing,
        IReadOnlyList<AiProviderModelOption> supportedModels,
        string selectedModelId,
        IReadOnlyList<AiProviderSettings> allProviders,
        CancellationToken cancellationToken)
    {
        if (IsAuxiliaryModelArtifact(selectedModelId))
        {
            return false;
        }

        var context = GetValidationContext(editor, existing);
        var nowUtc = DateTimeOffset.UtcNow;
        if (successfulTest is { } test && test.Context == context && test.ExpiresAt > nowUtc &&
            test.ModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (liveCatalog is { } catalog && catalog.Context == context && catalog.ExpiresAt > nowUtc)
        {
            return catalog.Models.Any(model => model.ModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase));
        }

        // Remote LMS engines have no model-list discovery API. Other providers must
        // validate against their current endpoint, not an old bundled/configured ID.
        if (editor.ProviderType == AiProviderType.RemoteLmsAiEngine)
        {
            return supportedModels.Any(model => model.ModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase)) ||
                existing?.DefaultModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase) == true;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var providerKey = existing?.ProviderKey ?? GenerateProviderKey(editor, allProviders);
            var settings = new AiProviderSettings(
                providerKey,
                editor.ProviderType,
                string.IsNullOrWhiteSpace(editor.DisplayName) ? providerKey : editor.DisplayName.Trim(),
                editor.IsEnabled,
                editor.IsDefault,
                ResolveBaseUrl(editor, existing),
                selectedModelId,
                editor.StreamingEnabled,
                editor.ToolUseEnabled,
                existing?.Notes ?? string.Empty,
                existing?.MetadataJson ?? string.Empty,
                editor.ClearStoredApiKey ? string.Empty : existing?.ApiKeySecretReference ?? string.Empty,
                existing?.CreatedAtUtc ?? now,
                now);

            var discoveredModels = await modelDiscoveryService.DiscoverAsync(settings, editor.ApiKeyInput, cancellationToken);
            liveCatalog = new(context, discoveredModels.Where(model => model.ProviderType == editor.ProviderType).ToArray(),
                DateTimeOffset.UtcNow + ValidationLifetime);
            return liveCatalog.Models.Any(model => model.ModelId.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Could not verify the model list. Refresh models or successfully test this model before saving. " + exception.Message,
                exception);
        }
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
            AiProviderType.Groq => "groq",
            AiProviderType.XAi => "xai-grok",
            AiProviderType.DeepSeek => "deepseek",
            AiProviderType.LinuxMadeSaneAiService => "linux-made-sane-ai-service",
            AiProviderType.Custom => "openai-compatible",
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

    private static string ResolveBaseUrl(AiProviderSettingsEditor editor, AiProviderSettings? existing) =>
        string.IsNullOrWhiteSpace(editor.BaseUrl)
            ? existing?.BaseUrl ?? string.Empty
            : editor.BaseUrl.Trim().TrimEnd('/');

    private static void NormalizeLinuxMadeSaneAiServiceEditor(AiProviderSettingsEditor editor)
    {
        if (editor.ProviderType != AiProviderType.LinuxMadeSaneAiService)
        {
            return;
        }

        editor.DisplayName = "Linux Made Sane AI Service";
        editor.DefaultModelId = "default";
        if (Uri.TryCreate(editor.BaseUrl.Trim(), UriKind.Absolute, out var serviceUri) &&
            serviceUri.Scheme is "http" or "https")
        {
            editor.BaseUrl = $"{serviceUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/v1";
        }
        editor.IsEnabled = true;
        editor.StreamingEnabled = false;
        editor.ToolUseEnabled = true;
        editor.RequiresApiKey = true;
    }

    private async Task DiscoverLinuxMadeSaneAiServiceModelAsync(
        AiProviderSettingsEditor editor,
        AiProviderSettings? existing,
        IReadOnlyList<AiProviderSettings> allProviders,
        CancellationToken cancellationToken)
    {
        if (editor.ProviderType != AiProviderType.LinuxMadeSaneAiService)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var settings = new AiProviderSettings(
            existing?.ProviderKey ?? GenerateProviderKey(editor, allProviders),
            editor.ProviderType,
            editor.DisplayName,
            true,
            editor.IsDefault,
            ResolveBaseUrl(editor, existing),
            editor.DefaultModelId,
            false,
            true,
            existing?.Notes ?? string.Empty,
            existing?.MetadataJson ?? string.Empty,
            editor.ClearStoredApiKey ? string.Empty : existing?.ApiKeySecretReference ?? string.Empty,
            existing?.CreatedAtUtc ?? now,
            now);
        var models = await modelDiscoveryService.DiscoverAsync(settings, editor.ApiKeyInput, cancellationToken);
        var model = models
            .OrderByDescending(item => item.IsRecommendedDefault)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (model is null)
        {
            throw new InvalidOperationException("The Linux Made Sane AI Service did not report an available Local AI model.");
        }

        editor.DefaultModelId = model.ModelId;
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

    private static bool IsAuxiliaryModelArtifact(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.Trim().Replace('\\', '/').ToLowerInvariant();
        var fileName = normalized.Split('/').LastOrDefault() ?? normalized;
        return fileName.StartsWith("mmproj", StringComparison.Ordinal) ||
               fileName.Contains("-mmproj", StringComparison.Ordinal) ||
               fileName.Contains("_mmproj", StringComparison.Ordinal) ||
               fileName.EndsWith(".mmproj", StringComparison.Ordinal) ||
               (fileName.Contains("projector", StringComparison.Ordinal) &&
                fileName.EndsWith(".gguf", StringComparison.Ordinal));
    }
}
