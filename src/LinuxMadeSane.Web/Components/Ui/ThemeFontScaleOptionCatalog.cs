// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Web.Components.Ui;

public static class ThemeFontScaleOptionCatalog
{
    public static IReadOnlyList<ThemeFontScaleOption> Options { get; } =
    [
        new(90, "90%"),
        new(100, "100%"),
        new(110, "110%"),
        new(120, "120%"),
        new(130, "130%"),
        new(140, "140%"),
        new(150, "150%"),
        new(160, "160%")
    ];

    public sealed record ThemeFontScaleOption(int Percent, string Label);
}
