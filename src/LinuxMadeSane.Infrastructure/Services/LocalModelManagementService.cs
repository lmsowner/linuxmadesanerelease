using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalModelManagementService : ILocalModelManagementService
{
    private static readonly IReadOnlyList<ModelTemplate> Templates =
    [
        new(
            "qwen2.5-coder:3b",
            "Qwen 2.5 Coder 3B",
            "Lowest-resource Qwen coder profile for constrained LMS hosts.",
            5L * 1024 * 1024 * 1024,
            4L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            SupportsTools: false,
            SupportsStreaming: true),
        new(
            "qwen2.5-coder:7b",
            "Qwen 2.5 Coder 7B",
            "Balanced local coding model and the default LMS local recommendation.",
            12L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            AiProviderCapabilityFlag.BasicChat |
            AiProviderCapabilityFlag.CommandExplanation |
            AiProviderCapabilityFlag.LogSummary |
            AiProviderCapabilityFlag.FixPlanGeneration |
            AiProviderCapabilityFlag.Streaming |
            AiProviderCapabilityFlag.DeepFixAllowedWithExtraApproval,
            SupportsTools: true,
            SupportsStreaming: true),
        new(
            "qwen2.5-coder:14b",
            "Qwen 2.5 Coder 14B",
            "Higher-capability local coding model for stronger private reasoning on larger LMS hosts.",
            24L * 1024 * 1024 * 1024,
            16L * 1024 * 1024 * 1024,
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
            "qwen2.5-coder:3b" => "This model is suitable for explanations and light planning, but Deep Fix should require extra approval.",
            "qwen2.5-coder:7b" => "This model is suitable for most explanations and fix plans. Mutating Deep Fix actions should still be reviewed carefully.",
            _ => "This local model can drive Deep Fix planning, but Linux Made Sane guardrails and approvals still apply."
        };

        return new LocalAiCapabilityReport(
            providerLabel,
            template.ModelId,
            capabilities,
            toolUseEnabled && template.SupportsTools,
            requiresExtraApproval,
            template.ModelId == "qwen2.5-coder:14b"
                ? "Strong local model for private coding, log analysis, and guarded Deep Fix planning."
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
            warning = "This model likely exceeds the host RAM available for comfortable local inference.";
        }
        else if (availableRam < template.EstimatedRamBytes + (2L * 1024 * 1024 * 1024))
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "This model may run, but memory headroom is tight and response times may be poor.";
        }
        else if (bestGpuVram > 0 && template.EstimatedVramBytes.HasValue && bestGpuVram < template.EstimatedVramBytes.Value)
        {
            suitability = LocalAiModelSuitability.Limited;
            warning = "GPU detected, but VRAM headroom is below the preferred size for this model.";
        }
        else if (availableRam < 8L * 1024 * 1024 * 1024 && !template.ModelId.Equals("qwen2.5-coder:3b", StringComparison.OrdinalIgnoreCase))
        {
            suitability = LocalAiModelSuitability.NotRecommended;
            warning = "This LMS host is below the practical RAM floor for mid-sized local models.";
        }
        else if (availableRam < 16L * 1024 * 1024 * 1024 && template.ModelId.Equals("qwen2.5-coder:14b", StringComparison.OrdinalIgnoreCase))
        {
            suitability = LocalAiModelSuitability.NotRecommended;
            warning = "The 14B profile is unrealistic on this RAM tier.";
        }
        else if (availableRam >= template.EstimatedRamBytes + (4L * 1024 * 1024 * 1024))
        {
            suitability = LocalAiModelSuitability.Recommended;
        }

        var isDefaultRecommendation = hardwareProfile.TotalMemoryBytes >= 16L * 1024 * 1024 * 1024
                                      ? template.ModelId.Equals("qwen2.5-coder:7b", StringComparison.OrdinalIgnoreCase)
                                      : template.ModelId.Equals("qwen2.5-coder:3b", StringComparison.OrdinalIgnoreCase);

        return template.ToDefinition(suitability, isDefaultRecommendation, warning);
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
