// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalAiUsageEntryEntity
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public int Scope { get; set; }
    public string? ConsumerOrganizationId { get; set; }
    public string? ConsumerInstanceId { get; set; }
    public string ConsumerDisplayName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public long DurationMilliseconds { get; set; }
    public int PromptCharacterCount { get; set; }
    public int OutputCharacterCount { get; set; }
    public bool UsedToolCalls { get; set; }
    public string ResultSummary { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
