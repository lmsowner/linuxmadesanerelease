// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class GroupEditor
{
    public Guid? Id { get; set; }

    [Required]
    public string GroupName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string MembersCsv { get; set; } = string.Empty;
}
