// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Globalization;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Scheduling;

namespace LinuxMadeSane.Application.Services;

public static class ScheduledTaskNextRunCalculator
{
    public static string DescribeNextRun(ScheduledTaskDefinition task, DateTimeOffset now)
    {
        if (!task.IsEnabled)
        {
            return "Paused";
        }

        if (task.ScheduleMode == ScheduledTaskScheduleMode.Reboot ||
            task.CronExpression.Equals("@reboot", StringComparison.OrdinalIgnoreCase))
        {
            return "At startup";
        }

        var nextRun = GetNextRun(task, now);
        if (!nextRun.HasValue)
        {
            return "Unavailable";
        }

        var localNextRun = nextRun.Value.ToLocalTime();
        return localNextRun.Date == now.ToLocalTime().Date
            ? $"Today {localNextRun:HH:mm}"
            : $"{localNextRun:dd MMM HH:mm}";
    }

    public static DateTimeOffset? GetNextRun(ScheduledTaskDefinition task, DateTimeOffset now) =>
        task.ScheduleMode switch
        {
            ScheduledTaskScheduleMode.Hourly => GetNextHourly(now, task.Minute),
            ScheduledTaskScheduleMode.Daily => GetNextDaily(now, task.Hour, task.Minute),
            ScheduledTaskScheduleMode.Weekly => GetNextWeekly(now, task.Hour, task.Minute, task.DaysOfWeekCsv),
            ScheduledTaskScheduleMode.Monthly => GetNextMonthly(now, task.Hour, task.Minute, task.DayOfMonth),
            ScheduledTaskScheduleMode.Reboot => null,
            _ => GetNextFromCronExpression(task.CronExpression, now)
        };

    private static DateTimeOffset GetNextHourly(DateTimeOffset now, int minute)
    {
        var candidate = TrimToMinute(now).AddMinutes(1);
        if (candidate.Minute <= minute)
        {
            return new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, minute, 0, candidate.Offset);
        }

        var nextHour = candidate.AddHours(1);
        return new DateTimeOffset(nextHour.Year, nextHour.Month, nextHour.Day, nextHour.Hour, minute, 0, nextHour.Offset);
    }

    private static DateTimeOffset GetNextDaily(DateTimeOffset now, int hour, int minute)
    {
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        return candidate > now ? candidate : candidate.AddDays(1);
    }

    private static DateTimeOffset GetNextWeekly(DateTimeOffset now, int hour, int minute, string daysOfWeekCsv)
    {
        var targetDays = daysOfWeekCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? day : -1)
            .Where(static day => day is >= 0 and <= 6)
            .Distinct()
            .OrderBy(static day => day)
            .ToArray();

        var start = TrimToMinute(now).AddMinutes(1);
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = start.Date.AddDays(offset);
            var candidate = new DateTimeOffset(day.Year, day.Month, day.Day, hour, minute, 0, now.Offset);
            if (targetDays.Contains((int)candidate.DayOfWeek) && candidate > now)
            {
                return candidate;
            }
        }

        return GetNextDaily(now, hour, minute);
    }

    private static DateTimeOffset GetNextMonthly(DateTimeOffset now, int hour, int minute, int dayOfMonth)
    {
        var year = now.Year;
        var month = now.Month;

        for (var i = 0; i < 24; i++)
        {
            var daysInMonth = DateTime.DaysInMonth(year, month);
            if (dayOfMonth <= daysInMonth)
            {
                var candidate = new DateTimeOffset(year, month, dayOfMonth, hour, minute, 0, now.Offset);
                if (candidate > now)
                {
                    return candidate;
                }
            }

            month++;
            if (month > 12)
            {
                month = 1;
                year++;
            }
        }

        return new DateTimeOffset(year, month, 1, hour, minute, 0, now.Offset);
    }

    private static DateTimeOffset? GetNextFromCronExpression(string cronExpression, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            return null;
        }

        var trimmed = cronExpression.Trim();
        if (trimmed.StartsWith('@'))
        {
            return trimmed.ToLowerInvariant() switch
            {
                "@hourly" => GetNextHourly(now, 0),
                "@daily" or "@midnight" => GetNextDaily(now, 0, 0),
                "@weekly" => GetNextWeekly(now, 0, 0, "0"),
                "@monthly" => GetNextMonthly(now, 0, 0, 1),
                "@yearly" or "@annually" => GetNextYearly(now),
                "@reboot" => null,
                _ => null
            };
        }

        var segments = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 5)
        {
            return null;
        }

        var minuteField = ParseField(segments[0], 0, 59, normalizeDayOfWeek: false);
        var hourField = ParseField(segments[1], 0, 23, normalizeDayOfWeek: false);
        var dayField = ParseField(segments[2], 1, 31, normalizeDayOfWeek: false);
        var monthField = ParseField(segments[3], 1, 12, normalizeDayOfWeek: false);
        var weekField = ParseField(segments[4], 0, 7, normalizeDayOfWeek: true);

        if (minuteField is null || hourField is null || dayField is null || monthField is null || weekField is null)
        {
            return null;
        }

        var candidate = TrimToMinute(now).AddMinutes(1);
        var limit = candidate.AddDays(366);

        while (candidate <= limit)
        {
            if (Matches(monthField, candidate.Month) &&
                Matches(hourField, candidate.Hour) &&
                Matches(minuteField, candidate.Minute) &&
                MatchesDay(candidate, dayField, weekField))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        return null;
    }

    private static DateTimeOffset GetNextYearly(DateTimeOffset now)
    {
        var candidate = new DateTimeOffset(now.Year + 1, 1, 1, 0, 0, 0, now.Offset);
        var thisYear = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
        return thisYear > now ? thisYear : candidate;
    }

    private static CronFieldSpec? ParseField(string segment, int minimum, int maximum, bool normalizeDayOfWeek)
    {
        var values = new HashSet<int>();
        var isWildcard = false;

        foreach (var token in segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var rangeToken = token;
            var slashIndex = token.IndexOf('/');
            if (slashIndex >= 0)
            {
                rangeToken = token[..slashIndex];
                if (!int.TryParse(token[(slashIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    return null;
                }
            }

            if (rangeToken == "*")
            {
                isWildcard = true;
                AddRange(values, minimum, maximum, step, normalizeDayOfWeek);
                continue;
            }

            var dashIndex = rangeToken.IndexOf('-');
            if (dashIndex >= 0)
            {
                if (!TryParseValue(rangeToken[..dashIndex], minimum, maximum, normalizeDayOfWeek, out var start) ||
                    !TryParseValue(rangeToken[(dashIndex + 1)..], minimum, maximum, normalizeDayOfWeek, out var end))
                {
                    return null;
                }

                if (start > end)
                {
                    return null;
                }

                AddRange(values, start, end, step, normalizeDayOfWeek);
                continue;
            }

            if (!TryParseValue(rangeToken, minimum, maximum, normalizeDayOfWeek, out var exact))
            {
                return null;
            }

            values.Add(exact);
        }

        return values.Count == 0 ? null : new CronFieldSpec(isWildcard, values);
    }

    private static bool TryParseValue(string token, int minimum, int maximum, bool normalizeDayOfWeek, out int value)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (normalizeDayOfWeek && value == 7)
        {
            value = 0;
        }

        return value >= minimum && value <= maximum;
    }

    private static void AddRange(HashSet<int> values, int start, int end, int step, bool normalizeDayOfWeek)
    {
        for (var value = start; value <= end; value += step)
        {
            values.Add(normalizeDayOfWeek && value == 7 ? 0 : value);
        }
    }

    private static bool Matches(CronFieldSpec field, int value) =>
        field.Values.Contains(value);

    private static bool MatchesDay(DateTimeOffset candidate, CronFieldSpec dayOfMonth, CronFieldSpec dayOfWeek)
    {
        var dayMatch = dayOfMonth.Values.Contains(candidate.Day);
        var weekMatch = dayOfWeek.Values.Contains((int)candidate.DayOfWeek);

        if (dayOfMonth.IsWildcard && dayOfWeek.IsWildcard)
        {
            return true;
        }

        if (dayOfMonth.IsWildcard)
        {
            return weekMatch;
        }

        if (dayOfWeek.IsWildcard)
        {
            return dayMatch;
        }

        return dayMatch || weekMatch;
    }

    private static DateTimeOffset TrimToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    private sealed record CronFieldSpec(
        bool IsWildcard,
        HashSet<int> Values);
}
