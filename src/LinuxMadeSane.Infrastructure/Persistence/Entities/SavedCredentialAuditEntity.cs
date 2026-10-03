// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SavedCredentialAuditEntity
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? CredentialId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
