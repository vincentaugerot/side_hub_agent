using System.Diagnostics;

namespace SideHub.Agent.Tests;

public class MachineSleepTests
{
    private static readonly DateTime Start = new(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);

    private static long Monotonic(TimeSpan elapsed) => (long)(elapsed.TotalSeconds * Stopwatch.Frequency);

    [Fact]
    public void A_tick_on_time_on_both_clocks_is_no_sleep()
    {
        var watch = new SleepWatch(Start, 0);

        Assert.Null(watch.Tick(Start.AddSeconds(15), Monotonic(TimeSpan.FromSeconds(15))));
        // A late tick (busy machine): both clocks went on
        Assert.Null(watch.Tick(Start.AddSeconds(75), Monotonic(TimeSpan.FromSeconds(75))));
    }

    [Fact]
    public void A_gap_on_the_wall_clock_only_is_the_time_asleep()
    {
        var watch = new SleepWatch(Start, 0);

        var slept = watch.Tick(Start.AddMinutes(20), Monotonic(TimeSpan.FromSeconds(15)));

        Assert.NotNull(slept);
        Assert.Equal(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(15), slept!.Value);
        // The next tick compares from the wake-up, not from before the sleep
        Assert.Null(watch.Tick(Start.AddMinutes(20).AddSeconds(15), Monotonic(TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public void A_gap_shorter_than_the_minimum_is_no_sleep()
    {
        var watch = new SleepWatch(Start, 0);

        Assert.Null(watch.Tick(Start.AddSeconds(40), Monotonic(TimeSpan.FromSeconds(15))));
    }

    private const string PmsetLaptop = """
        Battery Power:
         lidwake              1
         standby              1
         sleep                1
         powernap             1
        AC Power:
         lidwake              1
         sleep                10
         displaysleep         10
         powernap             1
        """;

    [Fact]
    public void Reads_the_sleep_of_the_ac_power_profile()
    {
        Assert.Equal(10, AgentService.AcSleepMinutes(PmsetLaptop));
        Assert.Equal(0, AgentService.AcSleepMinutes(PmsetLaptop.Replace("sleep                10", "sleep                0")));
    }

    [Fact]
    public void Reads_a_single_unnamed_profile_and_nothing_without_sleep()
    {
        Assert.Equal(1, AgentService.AcSleepMinutes(" sleep                1 (sleep prevented by powerd)\n displaysleep 10"));
        Assert.Null(AgentService.AcSleepMinutes("Battery Power:\n sleep 1\n"));
        Assert.Null(AgentService.AcSleepMinutes(""));
    }

    [Fact]
    public void Sleep_report_warns_with_the_fix_only_when_the_mac_sleeps()
    {
        Assert.Empty(AgentService.SleepLines(null));
        Assert.Single(AgentService.SleepLines(0), "[SideHub] This Mac does not go to sleep on its power adapter.");

        var lines = AgentService.SleepLines(10);

        Assert.StartsWith("[SideHub] ! This Mac goes to sleep after 10 min", lines[0]);
        Assert.Contains(lines, l => l.Contains("sudo pmset -c sleep 0"));
        Assert.Contains(lines, l => l.Contains("caffeinate -s"));
    }
}
