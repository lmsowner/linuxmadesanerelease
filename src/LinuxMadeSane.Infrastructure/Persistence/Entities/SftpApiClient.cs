// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SftpApiClient
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public string SourceNetwork { get; set; } = "";
}
