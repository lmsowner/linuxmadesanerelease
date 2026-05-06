using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiSafeChangeTarget(
    AiSafeChangeTargetKind Kind,
    string Name,
    string Detail);

public sealed record AiSafeChangeImpactPreview(
    string WhatWillChange,
    string WhyItIsNeeded,
    IReadOnlyList<AiSafeChangeTarget> AffectedTargets,
    string ServiceImpact,
    string ExpectedInterruption,
    string RiskSummary,
    IReadOnlyList<string> Warnings);

public sealed record AiSafeChangeRollbackPlan(
    AiRollbackSupportLevel SupportLevel,
    string Summary,
    IReadOnlyList<string> AutomaticRestoreSteps,
    IReadOnlyList<string> ManualSteps,
    string Limitations)
{
    public bool IsRollbackSupported =>
        SupportLevel is AiRollbackSupportLevel.Full or AiRollbackSupportLevel.Partial or AiRollbackSupportLevel.ManualOnly;
}

public sealed record AiSafeChangeVerificationPlan(
    string Summary,
    IReadOnlyList<string> Steps);

public sealed record AiSafeChangeFileSnapshot(
    string OriginalPath,
    bool Existed,
    string BackupPath,
    long SizeBytes,
    string ContentHash);

public sealed record AiSafeChangeServiceSnapshot(
    string ServiceName,
    bool WasActive,
    string EnabledState);

public sealed record AiSafeChangePackageState(
    string PackageName,
    bool WasInstalled,
    string Version);

public sealed record AiSafeChangePackageSnapshot(
    IReadOnlyList<AiSafeChangePackageState> Packages);

public sealed record AiSafeChangeSnapshot(
    Guid Id,
    DateTimeOffset CapturedAtUtc,
    string Summary,
    IReadOnlyList<string> CapturedItems,
    IReadOnlyList<string> Warnings)
{
    public AiSafeChangeFileSnapshot? File { get; init; }
    public AiSafeChangeServiceSnapshot? Service { get; init; }
    public AiSafeChangePackageSnapshot? Package { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record AiSafeChangeVerificationStepResult(
    string Label,
    bool Succeeded,
    string Details);

public sealed record AiSafeChangeVerificationResult(
    bool Succeeded,
    string Summary,
    IReadOnlyList<AiSafeChangeVerificationStepResult> Steps,
    DateTimeOffset VerifiedAtUtc);

public sealed record AiSafeChangeRollbackResult(
    AiExecutionOutcome Outcome,
    string Summary,
    IReadOnlyList<string> RestoredItems,
    IReadOnlyList<string> RemainingItems,
    DateTimeOffset ExecutedAtUtc)
{
    public AiSafeChangeVerificationResult? VerificationResult { get; init; }
}

public sealed record AiSafeChangeState(
    AiSafeChangeOperationKind OperationKind,
    AiSafeChangeImpactPreview ImpactPreview,
    AiSafeChangeRollbackPlan RollbackPlan,
    AiSafeChangeVerificationPlan VerificationPlan)
{
    public AiSafeChangeSnapshot? Snapshot { get; init; }
    public string ChangeSummary { get; init; } = string.Empty;
    public AiSafeChangeVerificationResult? VerificationResult { get; init; }
    public AiSafeChangeRollbackResult? RollbackResult { get; init; }

    public bool SupportsRollback =>
        Snapshot is not null && RollbackPlan.IsRollbackSupported;
}
