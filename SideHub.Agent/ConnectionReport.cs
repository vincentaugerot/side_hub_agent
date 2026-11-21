using SideHub.Agent.Update;

namespace SideHub.Agent;

/// <summary>
/// The agents that run without their connection to SideHub, read in the health file of their daemon (<c>status</c>,
/// <c>doctor</c>): seen from the machine, a daemon looks the same connected or not, its process and its PID are there.
/// </summary>
public static class ConnectionReport
{
    /// <summary>Under this, a reconnection is going on (a restart, a connection a proxy cut): not worth a line.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>One line per agent of the daemon running in <paramref name="directory"/> that has lost SideHub.</summary>
    public static IReadOnlyList<string> Lines(string directory, DateTime utcNow)
    {
        var health = UpdateFiles.Read<InstanceHealth>(UpdateFiles.HealthPath(directory));
        if (health?.Connecting is null)
            return [];
        // The file a stopped daemon left says nothing of the one running now
        var manager = new DaemonManager(directory);
        return manager.IsRunning() && manager.ReadPid() == health.Pid ? Lines(health.Connecting, utcNow) : [];
    }

    /// <summary>The lines themselves; pure so that it can be tested.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<AgentConnecting> connecting, DateTime utcNow) => connecting
        .Where(c => utcNow - c.Since >= Grace)
        .Select(c => $"{c.Agent}: not connected to SideHub for {Duration(utcNow - c.Since)} (since {c.Since:yyyy-MM-dd HH:mm} UTC)")
        .ToList();

    /// <summary><c>doctor</c>: every registered agent of the machine, and the folder the command runs in.</summary>
    public static void Print(string baseDirectory)
    {
        var directories = InstanceRegistry.LoadValid().Select(i => i.Directory).Append(Path.GetFullPath(baseDirectory))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var lines = directories.SelectMany(d => Lines(d, DateTime.UtcNow).Select(line => $"            {d}  {line}")).ToList();
        if (lines.Count == 0) return;

        Console.WriteLine("[SideHub] ! Running, but without a connection to SideHub:");
        foreach (var line in lines)
            Console.WriteLine(line);
        Console.WriteLine("          each keeps retrying; `sidehub-agent logs` in its folder says what stands in the way");
    }

    private static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours} h {duration.Minutes} min"
        : $"{(int)duration.TotalMinutes} min";
}
