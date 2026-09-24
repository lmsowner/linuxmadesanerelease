// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalModelManagementService : ILocalModelManagementService
{
    public const string BaselineModelId = "qwen3.5:4b";

    private static readonly IReadOnlyList<ModelTemplate> Templates =
    [
        new(
            "qwen3.5:0.8b",
            "Qwen 3.5 Tiny (0.8B)",
            "Fastest option for low-memory LMS hosts. Good for quick explanations when RAM is tight.",
            3L * 1024 * 1024 * 1024,
            2L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            SupportsTools: false,
            SupportsStreaming: true),
        new(
            "qwen3.5:2b",
            "Qwen 3.5 Light (2B)",
            "Light local model for smaller servers. Better answers than Tiny, still quick to download.",
            5L * 1024 * 1024 * 1024,
            3L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            SupportsTools: false,
            SupportsStreaming: true),
        new(
            BaselineModelId,
            "Qwen 3.5 Recommended (4B)",
            "Best everyday Local AI for most LMS hosts. Strong private chat, command help, and guarded fix planning.",
            8L * 1024 * 1024 * 1024,
            4L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.Streaming |
            AiProviderCapabilityFlag.ToolCalling |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            SupportsTools: true,
            SupportsStreaming: true),
        new(
            "qwen3.5:9b",
            "Qwen 3.5 Stronger (9B)",
            "Stronger local reasoning when this computer has spare RAM. Better for deeper planning and tool use.",
            14L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.Streaming |
            AiProviderCapabilityFlag.ToolCalling |
            AiProviderCapabilityFlag.DeepFixRecommended,
            SupportsTools: true,
            SupportsStreaming: true),
        new(
            "qwen3.5:27b",
            "Qwen 3.5 Heavy (27B)",
            "Large local model for high-memory hosts. Expect a bigger download and slower responses without a strong GPU.",
            32L * 1024 * 1024 * 1024,
            20L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.Streaming |
            AiProviderCapabilityFlag.ToolCalling |
            AiProviderCapabilityFlag.DeepFixRecommended,
            SupportsTools: true,
            SupportsStreaming: true)
    ];

    public IReadOnlyList<LocalAiModelDefinition> ListDefinitions() =>
        Templates.Select(template => template.ToDefinition(LocalAiModelSuitability.Supported, false, string.Empty)).ToArray();

    public IReadOnlyList<LocalAiModelDefinition> EvaluateForHardware(LocalAiHardwareProfile hardwareProfile) =>
        Templates
            .Select(template => EvaluateTemplate(template, hardwareProfile))
            .ToArray();

    public LocalAiModelDefinition? FindDefinition(string modelId)
    {
        var template = Templates.FirstOrDefault(item => item.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return template?.ToDefinition(LocalAiModelSuitability.Supported, false, string.Empty);
    }

    public LocalAiModelDefinition Recommend(LocalAiHardwareProfile hardwareProfile)
    {
        var evaluated = EvaluateForHardware(hardwareProfile)
            .OrderBy(model => model.Suitability)
            .ThenByDescending(model => model.IsDefaultRecommendation)
            .ThenBy(model => model.EstimatedRamBytes)
            .ToArray();

        return evaluated.FirstOrDefault(model => model.Suitability is LocalAiModelSuitability.Recommended or LocalAiModelSuitability.Supported)
               ?? evaluated.First();
    }

    public LocalAiCapabilityReport BuildCapabilityReport(string providerLabel, string modelId, bool toolUseEnabled)
    {
        var template = Templates.FirstOrDefault(item => item.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return new LocalAiCapabilityReport(
                providerLabel,
                modelId,
                AiProviderCapabilityFlag.BasicChat | AiProviderCapabilityFlag.CommandExplanation | AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
                false,
                true,
                "Unknown local model. Limit this provider to explanations, summaries, and carefully reviewed fix plans.",
                "Linux Made Sane cannot verify this local model’s tool-calling reliability. Mutating fixes should always require stronger approval.");
        }

        var capabilities = template.Capabilities;
        if (!toolUseEnabled)
        {
            capabilities &= ~AiProviderCapabilityFlag.ToolCalling;
        }

        var requiresExtraApproval = !capabilities.HasFlag(AiProviderCapabilityFlag.DeepFixRecommended);
        var warning = template.ModelId switch
        {
            "qwen3.5:0.8b" => "This tiny model is fast, but mutating Deep Fix actions should require extra approval.",
            "qwen3.5:2b" => "This light model is suitable for explanations and light planning, but Deep Fix should require extra approval.",
            BaselineModelId => "This recommended local model is suitable for most explanations and fix plans. Mutating Deep Fix actions should still be reviewed carefully.",
            _ => "This local model can drive Deep Fix planning, but Linux Made Sane guardrails and approvals still apply."
        };

        return new LocalAiCapabilityReport(
            providerLabel,
            template.ModelId,
            capabilities,
            toolUseEnabled && template.SupportsTools,
            requiresExtraApproval,
            template.ModelId == BaselineModelId
                ? "Best everyday local model for private chat, command help, and guarded Deep Fix planning."
                : template.ModelId is "qwen3.5:9b" or "qwen3.5:27b"
                    ? "Strong local model for private reasoning, log analysis, and guarded Deep Fix planning."
                    : "Local model suitable for explanations, log analysis, and guarded fix-plan generation.",
            warning);
    }

    private static LocalAiModelDefinition EvaluateTemplate(ModelTemplate template, LocalAiHardwareProfile hardwareProfile)
    {
        var availableRam = hardwareProfile.TotalMemoryBytes;
        var bestGpuVram = hardwareProfile.Gpus
            .Select(gpu => gpu.TotalVramBytes)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var suitability = LocalAiModelSuitability.Supported;
        var warning = string.Empty;

        if (availableRam < template.EstimatedRamBytes)
        {
            suitability = LocalAiModelSuitability.NotRecommended;
            warning = "This model likely exceeds the memory available for comfortable local AI.";
        }
        else if (availableRam < template.EstimatedRamBytes + (2L * 1024 * 1024 * 1024))
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "This model may run, but memory headroom is tight and responses may be slow.";
        }
        else if (bestGpuVram > 0 && template.EstimatedVramBytes.HasValue && bestGpuVram < template.EstimatedVramBytes.Value)
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "A GPU was detected, but VRAM is below the preferred size for this model.";
        }
        else if (availableRam < 6L * 1024 * 1024 * 1024 &&
                 !template.ModelId.Equals("qwen3.5:0.8b", StringComparison.OrdinalIgnoreCase))
        {
            suitability = LocalAiModelSuitability.NotRecommended;
            warning = "This computer is below the practical memory floor for mid-sized local models. Prefer Tiny.";
        }
        else if (availableRam < 8L * 1024 * 1024 * 1024 &&
                 template.ModelId is BaselineModelId or "qwen3.5:9b" or "qwen3.5:27b")
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "This model may run, but Tiny or Light will start faster on this computer.";
        }
        else if (availableRam < 16L * 1024 * 1024 * 1024 &&
                 template.ModelId.Equals("qwen3.5:9b", StringComparison.OrdinalIgnoreCase))
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "The Stronger profile prefers 16 GB or more RAM.";
        }
        else if (availableRam < 24L * 1024 * 1024 * 1024 &&
                 template.ModelId.Equals("qwen3.5:27b", StringComparison.OrdinalIgnoreCase))
        {
            suitability = LocalAiModelSuitability.NotRecommended;
            warning = "The Heavy profile is unrealistic on this memory tier.";
        }
        else if (availableRam < 48L * 1024 * 1024 * 1024 &&
                 template.ModelId.Equals("qwen3.5:27b", StringComparison.OrdinalIgnoreCase))
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "Heavy models need strong memory and GPU headroom. Startup and responses may be slow.";
        }
        else if (availableRam >= template.EstimatedRamBytes + (4L * 1024 * 1024 * 1024))
        {
            suitability = LocalAiModelSuitability.Recommended;
        }

        var defaultModelId = ResolveDefaultModelId(availableRam);
        var isDefaultRecommendation = template.ModelId.Equals(defaultModelId, StringComparison.OrdinalIgnoreCase);

        return template.ToDefinition(suitability, isDefaultRecommendation, warning);
    }

    private static string ResolveDefaultModelId(long availableRam)
    {
        if (availableRam < 6L * 1024 * 1024 * 1024)
        {
            return "qwen3.5:0.8b";
        }

        if (availableRam < 10L * 1024 * 1024 * 1024)
        {
            return "qwen3.5:2b";
        }

        if (availableRam >= 24L * 1024 * 1024 * 1024)
        {
            return "qwen3.5:9b";
        }

        return BaselineModelId;
    }

    private sealed record ModelTemplate(
        string ModelId,
        string DisplayName,
        string Description,
        long EstimatedRamBytes,
        long? EstimatedVramBytes,
        AiProviderCapabilityFlag Capabilities,
        bool SupportsTools,
        bool SupportsStreaming)
    {
        public LocalAiModelDefinition ToDefinition(
            LocalAiModelSuitability suitability,
            bool isDefaultRecommendation,
            string warning) =>
            new(
                ModelId,
                DisplayName,
                Description,
                EstimatedRamBytes,
                EstimatedVramBytes,
                SupportsTools,
                SupportsStreaming,
                suitability == LocalAiModelSuitability.Recommended,
                isDefaultRecommendation,
                suitability,
                Capabilities,
                warning);
    }
}
