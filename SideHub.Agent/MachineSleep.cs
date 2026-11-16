using System.Diagnostics;

namespace SideHub.Agent;

/// <summary>
/// Tells that the machine slept between two ticks of a timer: the wall clock went on, the monotonic clock did not
/// (<see cref="Stopwatch"/> reads CLOCK_UPTIME_RAW on macOS and CLOCK_MONOTONIC on Linux, both stopped while asleep).
/// A socket open before the sleep has gone silent without closing: the agent reconnects at once instead of waiting
/// for it to time out, and tells SideHub why it was away.
/// </summary>
public sealed class SleepWatch
{
    /// <summary>A shorter gap is a late tick (a busy machine, a GC pause), not a sleep.</summary>
    public static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(30);

    private DateTime _wall;
    private long _monotonic;

    public SleepWatch(DateTime wallNow, long monotonicNow)
    {
        _wall = wallNow;
        _monotonic = monotonicNow;
    }

    public static SleepWatch StartNow() => new(DateTime.UtcNow, Stopwatch.GetTimestamp());

    public TimeSpan? Tick() => Tick(DateTime.UtcNow, Stopwatch.GetTimestamp());

    /// <summary>How long the machine slept since the previous tick; null when it did not.</summary>
    public TimeSpan? Tick(DateTime wallNow, long monotonicNow)
    {
        var slept = (wallNow - _wall) - Stopwatch.GetElapsedTime(_monotonic, monotonicNow);
        _wall = wallNow;
        _monotonic = monotonicNow;
        return slept >= MinSleep ? slept : null;
    }
}
