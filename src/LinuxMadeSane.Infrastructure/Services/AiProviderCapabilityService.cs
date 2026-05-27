// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AiProviderCapabilityService(
    ILocalModelManagementService modelManagementService) : IAiProviderCapabilityService
{
    public Task<LocalAiCapabilityReport> AssessAsync(
        AiProviderSettings settings,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(modelId) ? settings.DefaultModelId : modelId.Trim();
        var report = settings.ProviderType switch
        {
            AiProviderType.OpenAi or AiProviderType.Anthropic or AiProviderType.Gemini or AiProviderType.Groq or AiProviderType.XAi or AiProviderType.DeepSeek => BuildManagedCloudReport(settings.DisplayName, effectiveModelId),
            AiProviderType.Ollama => modelManagementService.BuildCapabilityReport(settings.DisplayName, effectiveModelId, settings.ToolUseEnabled),
            AiProviderType.Custom => BuildCustomOpenAiCompatibleReport(settings, effectiveModelId),
            AiProviderType.RemoteLmsAiEngine => BuildRemoteReport(settings, effectiveModelId),
            _ => new LocalAiCapabilityReport(
                settings.DisplayName,
                effectiveModelId,
                AiProviderCapabilityFlag.BasicChat | AiProviderCapabilityFlag.CommandExplanation,
                false,
                true,
                "Unknown provider capability profile.",
                "Linux Made Sane cannot verify this provider’s Deep Fix capability. Mutating changes should require additional approval.")
        };

        return Task.FromResult(report);
    }

    private LocalAiCapabilityReport BuildRemoteReport(AiProviderSettings settings, string modelId)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<RemoteLmsAiEngineProviderMetadata>(settings.MetadataJson);
            if (metadata is not null)
            {
                return modelManagementService.BuildCapabilityReport(metadata.EngineDisplayName, modelId, settings.ToolUseEnabled) with
                {
                    Warning = "This AI turn is routed through another LMS instance. Deep Fix can continue, but Linux Made Sane should require stronger approval for risky changes."
                };
            }
        }
        catch (JsonException)
        {
        }

        return new LocalAiCapabilityReport(
            settings.DisplayName,
            modelId,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            false,
            true,
            "Remote LMS AI Engine suitable for chat, summaries, and guarded fix-plan generation.",
            "This provider executes through a remote LMS AI Engine. Deep Fix should require stronger approval for mutating actions.");
    }

    private static LocalAiCapabilityReport BuildManagedCloudReport(string providerLabel, string modelId) =>
        new(
            providerLabel,
            modelId,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.ToolCalling |
            AiProviderCapabilityFlag.Streaming |
            AiProviderCapabilityFlag.DeepFixRecommended,
            true,
            false,
            "Managed cloud provider with full LMS chat, tool-calling, and Deep Fix support.",
            string.Empty);

    private static LocalAiCapabilityReport BuildCustomOpenAiCompatibleReport(AiProviderSettings settings, string modelId) =>
        new(
            settings.DisplayName,
            modelId,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.Streaming |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            settings.ToolUseEnabled,
            true,
            "OpenAI-compatible local or Docker-hosted provider suitable for chat, summaries, and guarded fix-plan generation.",
            "Linux Made Sane cannot verify this self-hosted model's tool-calling reliability. Mutating Deep Fix actions should require stronger approval.");

}
