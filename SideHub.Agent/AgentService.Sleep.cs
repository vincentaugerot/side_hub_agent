namespace SideHub.Agent;

/// <summary>
/// <c>sidehub-agent doctor</c> on macOS: whether the Mac goes to sleep on its power adapter. Asleep, it answers no
/// heartbeat and its agents show offline until it wakes up (Power Nap and network wakes make it come and go).
/// </summary>
public static partial class AgentService
{
    public static void PrintSleepReport()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var result = Exec("pmset", "-g", "custom");
        if (!result.Ok) return;
        foreach (var line in SleepLines(AcSleepMinutes(result.Output)))
            Console.WriteLine(line);
    }

    /// <summary>
    /// The <c>sleep</c> setting (minutes, 0 = never) of the "AC Power:" profile of <c>pmset -g custom</c>, or of its
    /// only profile when it lists none; null when absent.
    /// </summary>
    public static int? AcSleepMinutes(string pmsetCustom)
    {
        string? profile = null;
        int? fallback = null;
        foreach (var raw in pmsetCustom.Split('\n'))
        {
            var line = raw.Trim();
            if (line.EndsWith(" Power:", StringComparison.Ordinal))
            {
                profile = line;
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "sleep" || !int.TryParse(parts[1], out var minutes)) continue;
            if (profile == "AC Power:") return minutes;
            if (profile is null) fallback = minutes;
        }
        return fallback;
    }

    /// <summary>The report, one line each; pure so that it can be tested.</summary>
    public static IReadOnlyList<string> SleepLines(int? acSleepMinutes) => acSleepMinutes switch
    {
        null => [],
        0 => ["[SideHub] This Mac does not go to sleep on its power adapter."],
        var minutes =>
        [
            $"[SideHub] ! This Mac goes to sleep after {minutes} min on its power adapter: its agents show offline while it sleeps.",
            "          turn it off: sudo pmset -c sleep 0   (or in System Settings: Battery > Options, or Energy on a desktop Mac)",
            "          or keep it awake while a terminal stays open: caffeinate -s",
        ],
    };
}
