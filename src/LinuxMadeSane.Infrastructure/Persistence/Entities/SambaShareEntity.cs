// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SambaShareEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SharePath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Browseable { get; set; }
    public bool ReadOnly { get; set; }
    public bool GuestAccess { get; set; }
    public string ValidUsersJson { get; set; } = "[]";
    public string ValidGroupsJson { get; set; } = "[]";
    public string WriteListJson { get; set; } = "[]";
    public string ReadListJson { get; set; } = "[]";
    public string? ForceUser { get; set; }
    public string? ForceGroup { get; set; }
    public string CreateMask { get; set; } = "0664";
    public string DirectoryMask { get; set; } = "2775";
    public string CreateMaskExplanation { get; set; } = string.Empty;
    public string DirectoryMaskExplanation { get; set; } = string.Empty;
}
