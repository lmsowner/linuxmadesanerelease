using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AiProviderConnectionTester(
    IAiProviderRegistry providerRegistry,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOllamaRuntimeService ollamaRuntimeService,
    IRemoteLmsAiEngineGateway remoteGateway) : IAiProviderConnectionTester
{
    public async Task<AiProviderConnectionTestResult> TestAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            var definition = providerRegistry.FindDefinition(settings.ProviderType);
            if (definition is null)
            {
                return new AiProviderConnectionTestResult(
                    false,
                    "Provider test failed.",
                    "The selected provider type is not supported.",
                    checkedAtUtc);
            }

            if (!definition.IsRuntimeImplemented)
            {
                return new AiProviderConnectionTestResult(
                    false,
                    "Provider test failed.",
                    string.IsNullOrWhiteSpace(definition.RuntimeNotes)
                        ? "That provider is not runnable in this build."
                        : definition.RuntimeNotes,
                    checkedAtUtc);
            }

            var provider = AiProviderRuntimeFactory.Create(
                definition,
                settings,
                BuildModels(settings),
                secretStore,
                httpClientFactory,
                ollamaRuntimeService,
                remoteGateway);

            var request = CreateRequest(settings, checkedAtUtc);
            var result = await provider.ExecuteTurnAsync(request, cancellationToken: timeoutCts.Token);

            var assistantText = string.Join(
                Environment.NewLine + Environment.NewLine,
                result.AssistantOutputs
                    .Select(output => output.Content?.Trim())
                    .Where(content => !string.IsNullOrWhiteSpace(content)));

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return new AiProviderConnectionTestResult(
                    false,
                    "Provider test failed.",
                    "The provider responded without any assistant text.",
                    checkedAtUtc);
            }

            return new AiProviderConnectionTestResult(
                true,
                "Provider test succeeded.",
                $"{settings.DisplayName} responded using {result.ModelId ?? settings.DefaultModelId}.",
                checkedAtUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiProviderConnectionTestResult(
                false,
                "Provider test timed out.",
                "The provider did not respond within 20 seconds.",
                checkedAtUtc);
        }
        catch (Exception exception)
        {
            return new AiProviderConnectionTestResult(
                false,
                "Provider test failed.",
                exception.Message,
                checkedAtUtc);
        }
    }

    private IReadOnlyList<AiModelDefinition> BuildModels(AiProviderSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultModelId))
        {
            return Array.Empty<AiModelDefinition>();
        }

        var catalogModel = providerRegistry.ListModelCatalog(settings.ProviderType)
            .FirstOrDefault(model => model.ModelId.Equals(settings.DefaultModelId, StringComparison.OrdinalIgnoreCase));

        return
        [
            new AiModelDefinition(
                settings.ProviderKey,
                settings.DefaultModelId,
                catalogModel?.DisplayName ?? settings.DefaultModelId,
                catalogModel?.Description ?? $"Default model for {settings.DisplayName}",
                null,
                catalogModel?.SupportsToolInvocation ?? settings.ToolUseEnabled)
        ];
    }

    private static AiProviderTurnRequest CreateRequest(AiProviderSettings settings, DateTimeOffset now) =>
        new(
            new AiChatThread(
                Guid.NewGuid(),
                "Provider validation",
                settings.ProviderKey,
                settings.ProviderType,
                settings.DefaultModelId,
                AiTrustProfile.CreatePreset(AiTrustLevel.Guided),
                string.Empty,
                string.Empty,
                now,
                now),
            [],
            [],
            [new AiProviderMessageInputItem(AiChatMessageRole.User, "Reply with exactly OK.")],
            [],
            false,
            false);
}
