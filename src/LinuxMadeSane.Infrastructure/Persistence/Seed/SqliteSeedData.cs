// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using System.Text.Json;

namespace LinuxMadeSane.Infrastructure.Persistence.Seed;

internal static class SqliteSeedData
{
    public static IReadOnlyList<ManagedHostEntity> ManagedHosts => [];

    public static IReadOnlyList<SavedCommandEntity> SavedCommands => [];

    public static IReadOnlyList<SambaShareEntity> SambaShares => [];

    private static string Serialize(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    public static IReadOnlyList<LinuxServiceEntity> LinuxServices => [];
}
