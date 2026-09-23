// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class HomeLabDeploymentEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? RecipeId { get; set; }
    public Guid? RecipeRunId { get; set; }
    public string? PromptRecipeId { get; set; }
    public string ConnectivityJson { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<HomeLabInstallationEntity> Installations { get; } = [];
}
