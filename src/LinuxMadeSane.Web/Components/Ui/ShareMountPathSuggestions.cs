// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Web.Components.Ui;

public static class ShareMountPathSuggestions
{
    public static string BuildLmsDefaultMountPath(string target, string shareName) =>
        $"/mnt/lms/{SlugifyPathSegment(target, "server")}/{SlugifyPathSegment(shareName, "share")}";

    public static string BuildMntMountPath(string target, string shareName) =>
        $"/mnt/{SlugifyPathSegment(target, "server")}/{SlugifyPathSegment(shareName, "share")}";

    public static string BuildHomeMountPath(string userName, string target, string shareName) =>
        $"/home/{SlugifyPathSegment(userName, "user")}/mnt/{SlugifyPathSegment(target, "server")}/{SlugifyPathSegment(shareName, "share")}";

    public static string SlugifyPathSegment(string value, string fallback)
    {
        var builder = new System.Text.StringBuilder();
        var previousWasSeparator = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
}
