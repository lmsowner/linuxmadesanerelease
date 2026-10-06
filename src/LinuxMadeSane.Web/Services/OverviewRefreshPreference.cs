// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Web.Services;

// The sidebar and Overview header share the existing polling presets and timer.
// This preference belongs to the current browser circuit, not every connected user.
public sealed class OverviewRefreshPreference
{
    public static readonly int[] IntervalsSeconds = [0, 1, 2, 5, 10, 30];
    public int PresetIndex { get; private set; } = 2;
    public event Func<Task>? Changed;
    public async Task SetAsync(int index)
    {
        index = Math.Clamp(index, 0, IntervalsSeconds.Length - 1);
        if (index == PresetIndex) return;
        PresetIndex = index;
        if (Changed is { } changed)
            foreach (Func<Task> handler in changed.GetInvocationList()) await handler();
    }
}
