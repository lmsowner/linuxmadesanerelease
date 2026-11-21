// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SavedCredentialAuditEntity
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? CredentialId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
