using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiProviderSettingsEditorContextViewModel(
    AiProviderSettingsEditor Editor,
    IReadOnlyList<AiProviderDefinition> SupportedProviders,
    IReadOnlyList<AiProviderModelOption> AvailableModelOptions,
    IReadOnlyList<AiConfiguredProviderViewModel> ExistingProviders,
    bool ProviderExists);
