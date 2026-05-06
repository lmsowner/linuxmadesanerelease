using LinuxMadeSane.Application.Contracts.LocalAi;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Application.Interfaces;

public interface ILocalAiEngineManagerService
{
    Task<LocalAiEngineWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default);
    Task<LocalAiSharingEditor> GetSharingEditorAsync(CancellationToken cancellationToken = default);
    Task<LocalAiSetupPlan> PreviewSetupAsync(LocalAiSetupEditor editor, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> ApplySetupAsync(
        LocalAiSetupEditor editor,
        bool approved,
        IProgress<LocalAiSetupProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> StartRuntimeAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> StopRuntimeAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> RestartRuntimeAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> PullModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> RemoveModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiBenchmarkResult> TestModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task<LocalAiBenchmarkResult> BenchmarkAsync(string modelId, CancellationToken cancellationToken = default);
    Task<LocalAiSharingEditor> SaveSharingAsync(LocalAiSharingEditor editor, CancellationToken cancellationToken = default);
    Task<AiProviderSettings> CreateOrUpdateLocalProviderAsync(string modelId, CancellationToken cancellationToken = default);
    Task<AiProviderSettings> CreateOrUpdateRemoteProviderAsync(
        string displayName,
        string modelId,
        RemoteLmsAiEngineReference reference,
        CancellationToken cancellationToken = default);
}
