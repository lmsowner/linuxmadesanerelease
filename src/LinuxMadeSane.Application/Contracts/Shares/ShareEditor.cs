// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class ShareEditor
{
    public Guid? Id { get; set; }

    [Required]
    [StringLength(128)]
    public string Name { get; set; } = string.Empty;

    [StringLength(255)]
    public string SharePath { get; set; } = string.Empty;

    [StringLength(512)]
    public string Description { get; set; } = string.Empty;

    public bool Browseable { get; set; } = true;
    public bool ReadOnly { get; set; }
    public bool GuestAccess { get; set; } = true;
    public string ValidUsersCsv { get; set; } = string.Empty;
    public string ValidGroupsCsv { get; set; } = string.Empty;
    public string WriteListCsv { get; set; } = string.Empty;
    public string ReadListCsv { get; set; } = string.Empty;
    public string ForceUser { get; set; } = string.Empty;
    public string ForceGroup { get; set; } = string.Empty;

    [Required]
    public string CreateMask { get; set; } = "0664";

    [Required]
    public string DirectoryMask { get; set; } = "2775";
}
