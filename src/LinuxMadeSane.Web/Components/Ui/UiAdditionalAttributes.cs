// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
