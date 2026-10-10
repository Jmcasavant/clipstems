using System.Diagnostics;

namespace Sidetrack.Core.Capture;

/// <summary>
/// Maps WASAPI packet timestamps (QPC converted to 100 ns units) to UTC
/// ticks, the clock Steam's metadata uses.
/// </summary>
public readonly record struct QpcClock(long OffsetTicks)
{
    public long ToUtcTicks(long qpc100ns) => qpc100ns + OffsetTicks;

    public static long Now100ns() => (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    /// <summary>
    /// Pairs the performance counter with system time. Takes the tightest of
    /// several bracketed reads so a preemption between the two reads does not
    /// skew every timestamp.
    /// </summary>
    public static QpcClock Anchor()
    {
        long bestWidth = long.MaxValue, bestOffset = 0;
        for (int i = 0; i < 7; i++)
        {
            long before = Now100ns();
            long utc = DateTime.UtcNow.Ticks;
            long after = Now100ns();
            if (after - before < bestWidth)
            {
                bestWidth = after - before;
                bestOffset = utc - (before + (after - before) / 2);
            }
        }
        return new QpcClock(bestOffset);
    }
}
