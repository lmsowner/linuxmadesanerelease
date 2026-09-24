// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Services;

public static class ConnectionProfileSelectionKeys
{
    public const string HostDefault = "host-default";
    public const string LinkedLinuxUser = "linked-linux-user";
    public const string Custom = "custom";

    private const string SavedPrefix = "saved:";

    public static string Saved(Guid profileId) => $"{SavedPrefix}{profileId:N}";

    public static bool TryParseSaved(string? value, out Guid profileId)
    {
        profileId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(SavedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParseExact(value[SavedPrefix.Length..], "N", out profileId);
    }
}
