// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.LocalAi;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Application.Services;

public sealed class LocalAiEngineManagerService(
    ILocalAiEngineStore store,
    ILocalAiHardwareInspectionService hardwareInspectionService,
    IOllamaRuntimeService ollamaRuntimeService,
    ILocalModelManagementService modelManagementService,
    IAiProviderCapabilityService capabilityService,
    IAiProviderSettingsStore providerSettingsStore,
    IRemoteLmsAiEngineGateway remoteGateway,
    IPortalConnectionStore portalConnectionStore,
    ILmsConnectClientFeature connectClientFeature) : ILocalAiEngineManagerService, ILocalAiEngineService
{
    private static readonly TimeSpan FreshPortalHeartbeatWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RemoteDiscoveryTimeout = TimeSpan.FromSeconds(3);

    public async Task<LocalAiEngineWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var statusTask = InspectAsync(cancellationToken);
        var benchmarksTask = store.ListBenchmarkResultsAsync(cancellationToken);
        var usageTask = store.ListUsageAsync(cancellationToken);
        var auditTask = store.ListAuditEntriesAsync(cancellationToken);
        var portalSettingsTask = portalConnectionStore.GetAsync(cancellationToken);

        await Task.WhenAll(statusTask, benchmarksTask, usageTask, auditTask, portalSettingsTask);

        var portalConnected = connectClientFeature.SupportsRemoteAiSharing &&
                              IsPortalConnected(portalSettingsTask.Result);
        IReadOnlyList<RemoteLmsAiEngineDescriptor> remoteEngines = [];
        if (portalConnected)
        {
            remoteEngines = await DiscoverSharedEnginesCoreAsync(cancellationToken);
        }

        return new LocalAiEngineWorkspaceViewModel(
            statusTask.Result,
            modelManagementService.EvaluateForHardware(statusTask.Result.Hardware),
            statusTask.Result.InstalledModels,
            benchmarksTask.Result,
            usageTask.Result,
            auditTask.Result,
            remoteEngines,
            portalConnected);
    }

    public async Task<LocalAiSharingEditor> GetSharingEditorAsync(CancellationToken cancellationToken = default)
    {
        EnsureSharingFeatureAvailable();
        var settings = await store.GetSettingsAsync(cancellationToken);
        return MapSharingEditor(settings);
    }

    public async Task<LocalAiEngineStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        var runtimeTask = ollamaRuntimeService.InspectAsync(cancellationToken);
        var hardwareTask = hardwareInspectionService.InspectAsync(cancellationToken);
        var settingsTask = store.GetSettingsAsync(cancellationToken);
        var installedModelsTask = RefreshInstalledModelsAsync(cancellationToken);
        var providersTask = providerSettingsStore.ListAsync(cancellationToken);

        await Task.WhenAll(runtimeTask, hardwareTask, settingsTask, installedModelsTask, providersTask);

        var settings = settingsTask.Result;
        var runtime = runtimeTask.Result;
        var hardware = hardwareTask.Result;
        var installedModels = installedModelsTask.Result;
        var effectiveModelId = string.IsNullOrWhiteSpace(settings.DefaultModelId)
            ? modelManagementService.Recommend(hardware).ModelId
            : settings.DefaultModelId;
        var capability = await capabilityService.AssessAsync(
            new AiProviderSettings(
                settings.LocalProviderKey,
                AiProviderType.Ollama,
                "Local Ollama",
                true,
                false,
                settings.RuntimeEndpoint,
                effectiveModelId,
                true,
                true,
                string.Empty,
                string.Empty,
                string.Empty,
                settings.CreatedAtUtc,
                settings.UpdatedAtUtc),
            effectiveModelId,
            cancellationToken);

        var configuredProviders = providersTask.Result;
        var localProviderConfigured = configuredProviders.Any(provider =>
            provider.ProviderType == AiProviderType.Ollama &&
            provider.ProviderKey.Equals(settings.LocalProviderKey, StringComparison.OrdinalIgnoreCase));
        var warnings = BuildWarnings(runtime, hardware, installedModels, settings);

        return new LocalAiEngineStatus(
            runtime,
            hardware,
            settings,
            installedModels,
            capability,
            localProviderConfigured,
            settings.SharingEnabled,
            warnings,
            DateTimeOffset.UtcNow);
    }

    public async Task<LocalAiSetupPlan> BuildSetupPlanAsync(
        string selectedModelId,
        bool enableSharing,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (enableSharing)
        {
            EnsureSharingFeatureAvailable();
        }

        var status = await InspectAsync(cancellationToken);
        var recommendedModel = string.IsNullOrWhiteSpace(selectedModelId)
            ? modelManagementService.Recommend(status.Hardware)
            : modelManagementService.FindDefinition(selectedModelId) ?? modelManagementService.Recommend(status.Hardware);
        var installed = status.InstalledModels.Any(model => model.ModelId.Equals(recommendedModel.ModelId, StringComparison.OrdinalIgnoreCase));
        var actions = new List<LocalAiPlanAction>();

        if (!status.Runtime.IsInstalled)
        {
            actions.Add(new LocalAiPlanAction(
                "Install Ollama",
                "Install the Ollama runtime and register its systemd service on this LMS host.",
                "curl -fsSL https://ollama.com/install.sh -o /tmp/lms-ollama-install.sh && sudo sh /tmp/lms-ollama-install.sh",
                "Adds the local AI runtime and systemd service.",
                true,
                true));
        }

        if (!status.Runtime.IsServiceActive)
        {
            actions.Add(new LocalAiPlanAction(
                "Start Ollama service",
                "Enable and start the Ollama systemd service.",
                "sudo systemctl enable --now ollama",
                "Makes the localhost Ollama API reachable to Linux Made Sane.",
                true,
                true));
        }

        if (!installed)
        {
            actions.Add(new LocalAiPlanAction(
                "Pull model",
                $"Pull {recommendedModel.ModelId} into the local Ollama model cache.",
                $"ollama pull {recommendedModel.ModelId}",
                "Consumes disk space and downloads the selected local model.",
                true,
                false));
        }

        actions.Add(new LocalAiPlanAction(
            "Test inference",
            "Run a small local test prompt against the selected model.",
            $"ollama run {recommendedModel.ModelId} \"Reply with exactly OK.\"",
            "Verifies that Linux Made Sane can talk to the local runtime.",
            false,
            false));

        actions.Add(new LocalAiPlanAction(
            "Configure provider",
            "Create or update the Linux Made Sane Local Ollama provider record.",
            $"Create provider {status.Settings.LocalProviderKey} with default model {recommendedModel.ModelId}",
            "Makes the local AI engine selectable by existing AI chat and Deep Fix.",
            true,
            false));

        if (enableSharing)
        {
            actions.Add(new LocalAiPlanAction(
                "Publish shared AI engine",
                "Register this LMS host as a shared AI engine through LMS Connect.",
                "Sync sharing metadata to LMS Portal",
                "Allows other authorized LMS instances to route AI requests here without exposing Ollama publicly.",
                true,
                false));
        }

        return new LocalAiSetupPlan(
            "Prepare this LMS host as a managed local AI engine.",
            recommendedModel.SuitabilityWarning.Length == 0
                ? $"Recommended model: {recommendedModel.DisplayName}."
                : $"Recommended model: {recommendedModel.DisplayName}. {recommendedModel.SuitabilityWarning}",
            installed ? 0 : recommendedModel.EstimatedRamBytes,
            recommendedModel.EstimatedRamBytes,
            true,
            false,
            "Linux Made Sane will verify Ollama service health and run a local inference test after changes.",
            actions);
    }

    public Task<LocalAiSetupPlan> PreviewSetupAsync(LocalAiSetupEditor editor, CancellationToken cancellationToken = default) =>
        BuildSetupPlanAsync(editor.SelectedModelId, editor.EnableSharing, true, cancellationToken);

    public async Task<LocalAiApplyResult> ApplySetupPlanAsync(
        string selectedModelId,
        bool enableSharing,
        bool approved,
        IProgress<LocalAiSetupProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (enableSharing)
        {
            EnsureSharingFeatureAvailable();
        }

        if (!approved)
        {
            ReportSetupProgress(
                progress,
                "Setup blocked",
                "Linux Made Sane requires explicit approval before it installs or configures the local AI engine.",
                LocalAiSetupProgressState.Failed);
            return new LocalAiApplyResult(
                false,
                "Setup blocked.",
                "Linux Made Sane requires explicit approval before installing or configuring the local AI engine.",
                false,
                [],
                DateTimeOffset.UtcNow);
        }

        ReportSetupProgress(
            progress,
            "Inspecting local AI runtime",
            "Checking Ollama, installed models, and current hardware before applying the setup plan.",
            LocalAiSetupProgressState.Running);
        var status = await InspectAsync(cancellationToken);
        var model = string.IsNullOrWhiteSpace(selectedModelId)
            ? modelManagementService.Recommend(status.Hardware)
            : modelManagementService.FindDefinition(selectedModelId) ?? modelManagementService.Recommend(status.Hardware);
        var output = new List<string>();

        if (!status.Runtime.IsInstalled)
        {
            ReportSetupProgress(
                progress,
                "Installing Ollama",
                "Downloading and installing the Ollama runtime, then enabling its systemd service.",
                LocalAiSetupProgressState.Running);
            var install = await ollamaRuntimeService.InstallAsync(true, cancellationToken);
            output.AddRange(install.OutputLines);
            if (!install.Succeeded)
            {
                ReportSetupProgress(
                    progress,
                    "Ollama install failed",
                    install.Detail,
                    LocalAiSetupProgressState.Failed);
                await RecordAuditAsync("local-ai.install.failed", "runtime", install.Summary, install.Detail, false, cancellationToken);
                return install;
            }

            ReportSetupProgress(
                progress,
                "Ollama installed",
                install.Detail,
                LocalAiSetupProgressState.Completed);
        }

        ReportSetupProgress(
            progress,
            "Refreshing runtime status",
            "Re-checking Ollama health and the currently installed models after any runtime changes.",
            LocalAiSetupProgressState.Running);
        status = await InspectAsync(cancellationToken);
        if (!status.InstalledModels.Any(item => item.ModelId.Equals(model.ModelId, StringComparison.OrdinalIgnoreCase)))
        {
            ReportSetupProgress(
                progress,
                $"Pulling model {model.ModelId}",
                $"Downloading {model.DisplayName} into the local Ollama model cache.",
                LocalAiSetupProgressState.Running);
            var pull = await ollamaRuntimeService.PullModelAsync(model.ModelId, true, cancellationToken);
            output.AddRange(pull.OutputLines);
            if (!pull.Succeeded)
            {
                ReportSetupProgress(
                    progress,
                    $"Model pull failed for {model.ModelId}",
                    pull.Detail,
                    LocalAiSetupProgressState.Failed);
                await RecordAuditAsync("local-ai.model.pull.failed", "models", pull.Summary, pull.Detail, false, cancellationToken);
                return pull;
            }

            ReportSetupProgress(
                progress,
                $"Model ready: {model.ModelId}",
                pull.Detail,
                LocalAiSetupProgressState.Completed);
        }

        ReportSetupProgress(
            progress,
            "Testing local inference",
            $"Running a small inference test against {model.ModelId}.",
            LocalAiSetupProgressState.Running);
        var benchmark = await ollamaRuntimeService.TestModelAsync(model.ModelId, cancellationToken);
        await store.SaveBenchmarkResultAsync(benchmark, cancellationToken);
        output.Add($"{benchmark.ModelId}: {benchmark.Detail}");
        if (!benchmark.Succeeded)
        {
            ReportSetupProgress(
                progress,
                "Local inference test failed",
                benchmark.Detail,
                LocalAiSetupProgressState.Failed);
            await RecordAuditAsync("local-ai.test.failed", "models", "Local AI model test failed.", benchmark.Detail, false, cancellationToken);
            return new LocalAiApplyResult(false, "Local model test failed.", benchmark.Detail, false, output, DateTimeOffset.UtcNow);
        }

        ReportSetupProgress(
            progress,
            "Local inference test passed",
            benchmark.Detail,
            LocalAiSetupProgressState.Completed);

        ReportSetupProgress(
            progress,
            "Registering local provider",
            $"Creating or updating the LMS Local Ollama provider with default model {model.ModelId}.",
            LocalAiSetupProgressState.Running);
        await CreateOrUpdateLocalProviderAsync(model.ModelId, cancellationToken);
        ReportSetupProgress(
            progress,
            "Local provider ready",
            "Existing AI chat and Deep Fix can now select the local Ollama provider.",
            LocalAiSetupProgressState.Completed);

        var currentSettings = await store.GetSettingsAsync(cancellationToken);
        ReportSetupProgress(
            progress,
            "Saving Local AI settings",
            "Persisting the default model and Local AI sharing settings.",
            LocalAiSetupProgressState.Running);
        var nextSettings = currentSettings with
        {
            DefaultModelId = model.ModelId,
            SharingEnabled = enableSharing,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastSharedAtUtc = enableSharing ? DateTimeOffset.UtcNow : currentSettings.LastSharedAtUtc
        };
        await store.SaveSettingsAsync(nextSettings, cancellationToken);
        ReportSetupProgress(
            progress,
            "Local AI settings saved",
            $"Default model is now {model.ModelId}. Sharing is {(enableSharing ? "enabled" : "disabled")}.",
            LocalAiSetupProgressState.Completed);

        var refreshedStatus = await InspectAsync(cancellationToken);
        if (enableSharing)
        {
            ReportSetupProgress(
                progress,
                "Publishing shared AI engine",
                "Syncing this AI engine to LMS Connect so other authorised LMS instances can use it securely.",
                LocalAiSetupProgressState.Running);
            await remoteGateway.SyncSharedEngineAsync(refreshedStatus, cancellationToken);
            ReportSetupProgress(
                progress,
                "Shared AI engine published",
                "The Local AI Engine is now available to authorised LMS instances through LMS Connect.",
                LocalAiSetupProgressState.Completed);
        }

        await RecordAuditAsync(
            "local-ai.setup.applied",
            "setup",
            "Local AI Engine configured.",
            $"Selected model: {model.ModelId}. Sharing enabled: {enableSharing}.",
            true,
            cancellationToken);

        ReportSetupProgress(
            progress,
            "Setup complete",
            $"Linux Made Sane can now use {model.DisplayName} through the local Ollama runtime.",
            LocalAiSetupProgressState.Completed);

        return new LocalAiApplyResult(
            true,
            "Local AI Engine configured.",
            $"Linux Made Sane can now use {model.DisplayName} through the local Ollama runtime.",
            false,
            output,
            DateTimeOffset.UtcNow);
    }

    public Task<LocalAiApplyResult> ApplySetupPlanAsync(
        string selectedModelId,
        bool enableSharing,
        bool approved,
        CancellationToken cancellationToken = default) =>
        ApplySetupPlanAsync(selectedModelId, enableSharing, approved, progress: null, cancellationToken);

    public Task<LocalAiApplyResult> ApplySetupAsync(
        LocalAiSetupEditor editor,
        bool approved,
        IProgress<LocalAiSetupProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        ApplySetupPlanAsync(editor.SelectedModelId, editor.EnableSharing, approved, progress, cancellationToken);

    public Task<LocalAiApplyResult> StartRuntimeAsync(bool approved, CancellationToken cancellationToken = default) =>
        ControlRuntimeAsync("local-ai.runtime.started", "runtime", "Local AI runtime started.", approved, ollamaRuntimeService.StartAsync, cancellationToken);

    public Task<LocalAiApplyResult> StopRuntimeAsync(bool approved, CancellationToken cancellationToken = default) =>
        ControlRuntimeAsync("local-ai.runtime.stopped", "runtime", "Local AI runtime stopped.", approved, ollamaRuntimeService.StopAsync, cancellationToken);

    public Task<LocalAiApplyResult> RestartRuntimeAsync(bool approved, CancellationToken cancellationToken = default) =>
        ControlRuntimeAsync("local-ai.runtime.restarted", "runtime", "Local AI runtime restarted.", approved, ollamaRuntimeService.RestartAsync, cancellationToken);

    public async Task<IReadOnlyList<LocalAiModelDefinition>> ListRecommendedModelsAsync(CancellationToken cancellationToken = default)
    {
        var hardware = await hardwareInspectionService.InspectAsync(cancellationToken);
        return modelManagementService.EvaluateForHardware(hardware);
    }

    public async Task<IReadOnlyList<LocalAiInstalledModel>> RefreshInstalledModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await ollamaRuntimeService.ListInstalledModelsAsync(cancellationToken);
        await store.ReplaceInstalledModelsAsync(models, cancellationToken);
        return models;
    }

    public async Task<LocalAiApplyResult> PullModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default)
    {
        var result = await ollamaRuntimeService.PullModelAsync(modelId, approved, cancellationToken);
        if (result.Succeeded)
        {
            await RefreshInstalledModelsAsync(cancellationToken);
            await RecordAuditAsync("local-ai.model.pulled", "models", "Local AI model pulled.", modelId, true, cancellationToken);
        }

        return result;
    }

    public async Task<LocalAiApplyResult> RemoveModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default)
    {
        var result = await ollamaRuntimeService.RemoveModelAsync(modelId, approved, cancellationToken);
        if (result.Succeeded)
        {
            await RefreshInstalledModelsAsync(cancellationToken);
            await RecordAuditAsync("local-ai.model.removed", "models", "Local AI model removed.", modelId, true, cancellationToken);
        }

        return result;
    }

    public async Task<LocalAiBenchmarkResult> TestModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var result = await ollamaRuntimeService.TestModelAsync(modelId, cancellationToken);
        await store.SaveBenchmarkResultAsync(result, cancellationToken);
        return result;
    }

    public async Task<LocalAiBenchmarkResult> BenchmarkAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var result = await ollamaRuntimeService.TestModelAsync(modelId, cancellationToken);
        await store.SaveBenchmarkResultAsync(result with
        {
            PromptSummary = "Linux Made Sane benchmark prompt"
        }, cancellationToken);
        return result;
    }

    public async Task<LocalAiEngineSettings> SaveSharingSettingsAsync(LocalAiEngineSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureSharingFeatureAvailable();
        var next = settings with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        await store.SaveSettingsAsync(next, cancellationToken);
        var status = await InspectAsync(cancellationToken);
        await remoteGateway.SyncSharedEngineAsync(status, cancellationToken);
        await RecordAuditAsync("local-ai.sharing.saved", "sharing", "Local AI Engine sharing settings updated.", string.Empty, true, cancellationToken);
        return next;
    }

    public async Task<LocalAiSharingEditor> SaveSharingAsync(LocalAiSharingEditor editor, CancellationToken cancellationToken = default)
    {
        EnsureSharingFeatureAvailable();
        ValidateEditor(editor);
        var current = await store.GetSettingsAsync(cancellationToken);
        var next = current with
        {
            SharingEnabled = editor.SharingEnabled,
            AllowOrganizationInstances = editor.AllowOrganizationInstances,
            AllowedInstanceIds = ParseGuids(editor.AllowedInstanceIdsText),
            AllowedOrganizationIds = ParseGuids(editor.AllowedOrganizationIdsText),
            AllowedModelIds = ParseStrings(editor.AllowedModelIdsText),
            MaxConcurrentRequests = editor.MaxConcurrentRequests,
            MaxQueuedRequests = editor.MaxQueuedRequests,
            MaxRequestsPerMinute = editor.MaxRequestsPerMinute,
            MaxPromptCharacters = editor.MaxPromptCharacters,
            RequestTimeoutSeconds = editor.RequestTimeoutSeconds,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastSharedAtUtc = editor.SharingEnabled ? DateTimeOffset.UtcNow : current.LastSharedAtUtc
        };

        await SaveSharingSettingsAsync(next, cancellationToken);
        return MapSharingEditor(next);
    }

    public async Task<IReadOnlyList<RemoteLmsAiEngineDescriptor>> DiscoverSharedEnginesAsync(CancellationToken cancellationToken = default)
    {
        if (!connectClientFeature.SupportsRemoteAiSharing)
        {
            return [];
        }

        var portalSettings = await portalConnectionStore.GetAsync(cancellationToken);
        return IsPortalConnected(portalSettings)
            ? await DiscoverSharedEnginesCoreAsync(cancellationToken)
            : [];
    }

    public async Task<AiProviderSettings> CreateOrUpdateLocalProviderAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        var providerKey = string.IsNullOrWhiteSpace(settings.LocalProviderKey) ? "local-ollama" : settings.LocalProviderKey;
        var existingProviders = await providerSettingsStore.ListAsync(cancellationToken);
        var existing = existingProviders.FirstOrDefault(provider => provider.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;
        var shouldBeDefault = existing?.IsDefault == true || existingProviders.All(provider => !provider.IsDefault);

        var provider = new AiProviderSettings(
            providerKey,
            AiProviderType.Ollama,
            "Local Ollama",
            true,
            shouldBeDefault,
            settings.RuntimeEndpoint,
            modelId.Trim(),
            true,
            true,
            "Linux Made Sane managed local AI engine.",
            string.Empty,
            string.Empty,
            existing?.CreatedAtUtc ?? now,
            now);

        await providerSettingsStore.SaveAsync(provider, cancellationToken);
        if (shouldBeDefault)
        {
            foreach (var other in existingProviders.Where(item =>
                         !item.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase) &&
                         item.IsDefault))
            {
                await providerSettingsStore.SaveAsync(other with
                {
                    IsDefault = false,
                    UpdatedAtUtc = now
                }, cancellationToken);
            }
        }

        var engineSettings = settings with
        {
            LocalProviderKey = providerKey,
            DefaultModelId = modelId.Trim(),
            UpdatedAtUtc = now
        };
        await store.SaveSettingsAsync(engineSettings, cancellationToken);
        return provider;
    }

    public async Task<AiProviderSettings> CreateOrUpdateRemoteProviderAsync(
        string displayName,
        string modelId,
        RemoteLmsAiEngineReference reference,
        CancellationToken cancellationToken = default)
    {
        EnsureSharingFeatureAvailable();

        var existingProviders = await providerSettingsStore.ListAsync(cancellationToken);
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? reference.EngineDisplayName
            : displayName.Trim();
        var providerKeyBase = $"remote-ai-{reference.ProviderInstanceId:N}";
        var providerKey = existingProviders.Any(provider => provider.ProviderKey.Equals(providerKeyBase, StringComparison.OrdinalIgnoreCase))
            ? providerKeyBase
            : providerKeyBase;
        var existing = existingProviders.FirstOrDefault(provider => provider.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;

        var provider = new AiProviderSettings(
            providerKey,
            AiProviderType.RemoteLmsAiEngine,
            normalizedDisplayName,
            true,
            false,
            string.Empty,
            modelId.Trim(),
            true,
            true,
            "Linux Made Sane managed remote AI engine provider.",
            JsonSerializer.Serialize(new RemoteLmsAiEngineProviderMetadata(
                reference.OrganizationId,
                reference.ProviderInstanceId,
                reference.EngineDisplayName,
                reference.InstanceDisplayName,
                reference.AllowedModelIds)),
            string.Empty,
            existing?.CreatedAtUtc ?? now,
            now);

        await providerSettingsStore.SaveAsync(provider, cancellationToken);
        await RecordAuditAsync("local-ai.remote-provider.saved", "sharing", "Remote LMS AI Engine provider saved.", normalizedDisplayName, true, cancellationToken);
        return provider;
    }

    private static LocalAiSharingEditor MapSharingEditor(LocalAiEngineSettings settings) =>
        new()
        {
            SharingEnabled = settings.SharingEnabled,
            AllowOrganizationInstances = settings.AllowOrganizationInstances,
            MaxConcurrentRequests = settings.MaxConcurrentRequests,
            MaxQueuedRequests = settings.MaxQueuedRequests,
            MaxRequestsPerMinute = settings.MaxRequestsPerMinute,
            MaxPromptCharacters = settings.MaxPromptCharacters,
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            AllowedInstanceIdsText = string.Join(Environment.NewLine, settings.AllowedInstanceIds.Select(item => item.ToString("D"))),
            AllowedOrganizationIdsText = string.Join(Environment.NewLine, settings.AllowedOrganizationIds.Select(item => item.ToString("D"))),
            AllowedModelIdsText = string.Join(Environment.NewLine, settings.AllowedModelIds)
        };

    private async Task RecordAuditAsync(
        string eventType,
        string scope,
        string summary,
        string detail,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        await store.SaveAuditEntryAsync(
            new LocalAiAuditEntry(Guid.NewGuid(), eventType, scope, summary, detail, succeeded, DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task<LocalAiApplyResult> ControlRuntimeAsync(
        string auditEventType,
        string scope,
        string successSummary,
        bool approved,
        Func<bool, CancellationToken, Task<LocalAiApplyResult>> action,
        CancellationToken cancellationToken)
    {
        var result = await action(approved, cancellationToken);
        await RecordAuditAsync(
            auditEventType,
            scope,
            successSummary,
            result.Detail,
            result.Succeeded,
            cancellationToken);
        return result;
    }

    private static IReadOnlyList<string> BuildWarnings(
        LocalAiRuntime runtime,
        LocalAiHardwareProfile hardware,
        IReadOnlyList<LocalAiInstalledModel> installedModels,
        LocalAiEngineSettings settings)
    {
        var warnings = new List<string>();
        if (!runtime.IsInstalled)
        {
            warnings.Add("Ollama is not installed on this LMS host.");
        }

        if (hardware.TotalMemoryBytes > 0 && hardware.TotalMemoryBytes < 8L * 1024 * 1024 * 1024)
        {
            warnings.Add("This LMS host has less than 8 GB RAM. Local AI is possible, but only very small models are realistic.");
        }

        if (hardware.GpuAccelerationState == LocalAiGpuAccelerationState.Available)
        {
            warnings.Add("GPU detected, but Linux Made Sane could not confirm CUDA or ROCm acceleration.");
        }

        if (!installedModels.Any())
        {
            warnings.Add("No local models are installed yet.");
        }

        if (settings.SharingEnabled && !runtime.IsApiReachable)
        {
            warnings.Add("Sharing is enabled, but the local Ollama API is not currently reachable.");
        }

        return warnings;
    }

    private static IReadOnlyList<Guid> ParseGuids(string value) =>
        value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => Guid.TryParse(item, out var parsed) ? parsed : Guid.Empty)
            .Where(item => item != Guid.Empty)
            .Distinct()
            .ToArray();

    private static IReadOnlyList<string> ParseStrings(string value) =>
        value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateEditor(LocalAiSharingEditor editor)
    {
        var context = new ValidationContext(editor);
        Validator.ValidateObject(editor, context, validateAllProperties: true);
    }

    private static void ReportSetupProgress(
        IProgress<LocalAiSetupProgressUpdate>? progress,
        string step,
        string detail,
        LocalAiSetupProgressState state) =>
        progress?.Report(new LocalAiSetupProgressUpdate(step, detail, state, DateTimeOffset.UtcNow));

    private void EnsureSharingFeatureAvailable()
    {
        if (!connectClientFeature.SupportsRemoteAiSharing)
        {
            throw new InvalidOperationException("The LMS Connect client plugin is not installed in this build.");
        }
    }

    private async Task<IReadOnlyList<RemoteLmsAiEngineDescriptor>> DiscoverSharedEnginesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RemoteDiscoveryTimeout);
            return await remoteGateway.DiscoverAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPortalConnected(PortalConnectionSettings? settings) =>
        settings is not null &&
        settings.IsEnabled &&
        settings.PortalInstanceId.HasValue &&
        !string.IsNullOrWhiteSpace(settings.PortalApiKeyId) &&
        !string.IsNullOrWhiteSpace(settings.PortalApiSecretReference) &&
        settings.LastHeartbeatAtUtc.HasValue &&
        DateTimeOffset.UtcNow - settings.LastHeartbeatAtUtc.Value <= FreshPortalHeartbeatWindow;
}
