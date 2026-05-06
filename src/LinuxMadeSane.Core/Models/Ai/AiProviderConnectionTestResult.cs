namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderConnectionTestResult(
    bool Succeeded,
    string Summary,
    string? Detail,
    DateTimeOffset CheckedAtUtc);
