// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Persistence.Entities;
public sealed class RemoteDesktopConnectionEntity
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? HostId { get; set; }
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public int Protocol { get; set; }
    public int Port { get; set; }
    public Guid CredentialId { get; set; }
    public string OptionsJson { get; set; } = "{}";
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
