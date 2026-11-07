// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Licensing;
public sealed record LicensingClockReading(DateTimeOffset WallUtc, DateTimeOffset ConservativeUtc);
public sealed class LicensingClock(TimeProvider? provider = null)
{
    private readonly TimeProvider time = provider ?? TimeProvider.System;
    private readonly object gate = new();
    private DateTimeOffset? anchorUtc;
    private long anchorTimestamp;
    private DateTimeOffset maximumUtc;
    public LicensingClockReading Read()
    {
        lock (gate)
        {
            var wall = time.GetUtcNow();
            if (anchorUtc is null) { anchorUtc = wall; anchorTimestamp = time.GetTimestamp(); maximumUtc = wall; }
            var elapsed = anchorUtc.Value + time.GetElapsedTime(anchorTimestamp);
            if (elapsed > maximumUtc) maximumUtc = elapsed;
            if (wall > maximumUtc) maximumUtc = wall;
            return new(wall, maximumUtc);
        }
    }
}
