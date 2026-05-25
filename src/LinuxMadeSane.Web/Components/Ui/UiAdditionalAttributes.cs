// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Components.Ui;

internal static class UiAdditionalAttributes
{
    public static IReadOnlyDictionary<string, object>? Sanitize(IReadOnlyDictionary<string, object>? attributes)
    {
        if (attributes is null || attributes.Keys.All(static key => !key.StartsWith('@')))
        {
            return attributes;
        }

        return attributes
            .Where(static pair => !pair.Key.StartsWith('@'))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
