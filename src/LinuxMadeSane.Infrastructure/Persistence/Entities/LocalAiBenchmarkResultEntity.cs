// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalAiBenchmarkResultEntity
{
    public Guid Id { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string PromptSummary { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public long DurationMilliseconds { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset ExecutedAtUtc { get; set; }
}
