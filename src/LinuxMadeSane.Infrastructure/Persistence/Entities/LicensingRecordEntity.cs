// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Persistence.Entities;
public sealed class LicensingRecordEntity
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; }
    public string Json { get; set; } = "{}";
}
