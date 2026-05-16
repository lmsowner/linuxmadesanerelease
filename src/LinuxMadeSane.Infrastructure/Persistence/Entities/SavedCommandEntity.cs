// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SavedCommandEntity
{
    public Guid Id { get; set; }
    public Guid HostId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresSudo { get; set; }
    public bool IsQuickAccess { get; set; }
    public bool IsGlobalFavorite { get; set; }
    public bool IsTemplate { get; set; }
    public Guid? TemplateSourceId { get; set; }
    public Guid? LinkGroupId { get; set; }
    public string ParameterDefinitionsJson { get; set; } = "[]";
    public string ParameterValueSnapshotJson { get; set; } = "{}";

    public ManagedHostEntity? Host { get; set; }
}
