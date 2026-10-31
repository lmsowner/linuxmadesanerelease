// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
namespace LinuxMadeSane.Core.Abstractions;

public sealed record HostIpv4Addresses(string? InternalIpv4,string? TailscaleIpv4);
public interface IHostIpv4AddressService
{
    HostIpv4Addresses ReadPrimaryIpv4();
}
