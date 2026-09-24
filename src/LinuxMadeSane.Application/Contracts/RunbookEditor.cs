// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts;

public sealed class RunbookEditor
{
    public Guid? Id { get; set; }

    [Required]
    public Guid HostId { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [StringLength(320)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [StringLength(32000)]
    public string CommandText { get; set; } = string.Empty;

    public bool RequiresSudo { get; set; }

    public bool IsQuickAccess { get; set; }

    public bool IsGlobalFavorite { get; set; }

    public bool IsTemplate { get; set; }

    public Guid? TemplateSourceId { get; set; }

    public string TemplateSourceName { get; set; } = string.Empty;

    public Guid? LinkGroupId { get; set; }

    public RunbookDistributionMode DistributionMode { get; set; } = RunbookDistributionMode.SingleMachine;

    public List<Guid> SelectedHostIds { get; set; } = [];

    public List<RunbookParameterEditor> Parameters { get; set; } = [];
}
