// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;

namespace LinuxMadeSane.Web.Services;

internal static class SystemInfoValueFormatter
{
    private const double UnitSize = 1_000d;
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string FormatDriveSize(long bytes) => FormatSize(bytes, decimalPlaces: 2);

    public static string FormatRateSize(double bytes) => FormatSize(bytes, decimalPlaces: 1);

    private static string FormatSize(double bytes, int decimalPlaces)
    {
        var value = Math.Max(0d, bytes);
        var unit = 0;
        while (Math.Round(value, decimalPlaces) >= UnitSize && unit < Units.Length - 1)
        {
            value /= UnitSize;
            unit++;
        }

        return $"{value.ToString($"N{decimalPlaces}", CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
