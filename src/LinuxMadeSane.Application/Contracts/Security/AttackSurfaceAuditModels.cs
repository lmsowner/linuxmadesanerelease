// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public enum AttackSurfaceFindingLevel
{
    Protected = 0,
    Review = 1,
    Attention = 2
}

public sealed record AttackSurfaceFinding(
    string Id,
    string Category,
    string Title,
    AttackSurfaceFindingLevel Level,
    string Summary,
    string Evidence,
    string Recommendation);

public sealed record AttackSurfaceAiAvailability(
    bool IsAvailable,
    string ProviderKey,
    string ProviderName,
    string ModelId);

public sealed record AttackSurfacePosture(
    AttackSurfaceFindingLevel Level,
    string Label,
    string Summary,
    IReadOnlyList<string> ConfirmedControls,
    string ConfidenceNote);

public sealed record AttackSurfaceAuditResult(
    DateTimeOffset CapturedAtUtc,
    AttackSurfacePosture Posture,
    IReadOnlyList<AttackSurfaceFinding> Findings,
    IReadOnlyList<FirewallListeningPortViewModel> ListeningPorts,
    AttackSurfaceAiAvailability AiAvailability,
    string? AiAnalysis,
    string? AiAnalysisError)
{
    public int AttentionCount => Findings.Count(finding => finding.Level == AttackSurfaceFindingLevel.Attention);

    public int ReviewCount => Findings.Count(finding => finding.Level == AttackSurfaceFindingLevel.Review);

    public int ProtectedCount => Findings.Count(finding => finding.Level == AttackSurfaceFindingLevel.Protected);
}
