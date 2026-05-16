// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services;

public sealed record HttpServiceDiscoveryStorageSettings(string RootDirectory)
{
    public string CachePath => Path.Combine(RootDirectory, "http-services-cache.json");
}
