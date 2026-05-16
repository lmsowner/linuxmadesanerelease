// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class RemoteShareMountEntity
{
    public Guid Id { get; set; }
    public string RemoteHost { get; set; } = string.Empty;
    public string? RemoteAddress { get; set; }
    public string ShareName { get; set; } = string.Empty;
    public string LocalMountPath { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    public string? CredentialFilePath { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastMountedAtUtc { get; set; }
}
