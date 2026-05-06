using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiChatThreadEditorContextViewModel(
    AiChatThreadEditor Editor,
    IReadOnlyList<ManagedHost> AvailableServers,
    IReadOnlyList<AiProviderDefinition> SupportedProviders,
    IReadOnlyList<AiConfiguredProviderViewModel> ConfiguredProviders,
    IReadOnlyList<AiModelDefinition> Models);
