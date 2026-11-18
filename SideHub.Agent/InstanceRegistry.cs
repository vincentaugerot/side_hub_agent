using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent;

/// <summary>
/// <c>~/.sidehub/instances.json</c>: the projects whose agent runs on this machine, read by <c>--all</c> commands,
/// the boot entry and the one-click update. Every daemon registers itself when it starts, so a machine with many
/// projects writes it from many processes in the same second (a grouped restart after an update or a reboot).
/// <para>So a write replaces the file in one rename (a reader never sees it half written), a read then write holds
/// <c>instances.lock</c> (no lost update between two daemons), and a file that stays unreadable is set aside as
/// <c>instances.json.corrupt</c> instead of being overwritten with a single entry (the other projects would vanish
/// from it silently).</para>
/// </summary>
public static class InstanceRegistry
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sidehub",
        "instances.json");

    private const int ReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private const int ReplaceAttempts = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<InstanceEntry> LoadAll() => LoadAll(DefaultPath);

    /// <summary>The registered entries; empty when the registry is missing or stays unreadable.</summary>
    public static List<InstanceEntry> LoadAll(string registryPath)
    {
        if (TryLoad(registryPath) is { } entries)
            return entries;

        Console.WriteLine($"[SideHub] Warning: corrupted registry at {registryPath}, treating as empty");
        return [];
    }

    public static List<InstanceEntry> LoadValid()
    {
        return LoadAll()
            .Where(e => Directory.Exists(Path.Combine(e.Directory, ".sidehub")))
            .ToList();
    }

    public static void Register(string directory) => Register(directory, DefaultPath);

    public static void Register(string directory, string registryPath)
    {
        var absPath = Path.GetFullPath(directory);
        Update(registryPath, entries =>
        {
            entries.RemoveAll(e => string.Equals(e.Directory, absPath, StringComparison.Ordinal));
            entries.Add(new InstanceEntry { Directory = absPath, RegisteredAt = DateTime.UtcNow });
        });
    }

    public static void Unregister(string directory) => Unregister(directory, DefaultPath);

    public static void Unregister(string directory, string registryPath)
    {
        var absPath = Path.GetFullPath(directory);
        Update(registryPath, entries =>
            entries.RemoveAll(e => string.Equals(e.Directory, absPath, StringComparison.Ordinal)));
    }

    /// <summary>The entries, empty when the file is missing, or null when it stays unparsable after a few reads
    /// (a writer of an older version may be rewriting it in place).</summary>
    private static List<InstanceEntry>? TryLoad(string registryPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(registryPath))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<InstanceEntry>>(File.ReadAllText(registryPath), JsonOptions) ?? [];
            }
            catch (JsonException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(ReadRetryDelay);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static void Update(string registryPath, Action<List<InstanceEntry>> change)
    {
        // The registry lists the user's projects: ~/.sidehub is 0700, instances.json and its lock 0600, set at
        // creation (and tightened when left by an older version) rather than left to the umask.
        try
        {
            PrivateFiles.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            using var registryLock = AcquireLock(registryPath);
            var entries = TryLoad(registryPath) ?? SetAsideUnreadable(registryPath);
            change(entries);
            Save(registryPath, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The agent runs without it; only `start --all` / `stop --all`, the boot entry and updates miss this project.
            Console.WriteLine($"[SideHub] Warning: couldn't update {registryPath}: {ex.Message}");
        }
    }

    /// <summary>Holds <c>instances.lock</c> until disposed, retrying while another process holds it. Null after
    /// <see cref="LockTimeout"/>: the agent then writes without it rather than not starting.</summary>
    private static FileStream? AcquireLock(string registryPath)
    {
        var lockPath = Path.Combine(Path.GetDirectoryName(registryPath)!, "instances.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return PrivateFiles.OpenExclusive(lockPath);
            }
            catch (IOException) when (elapsed.Elapsed < LockTimeout)
            {
                Thread.Sleep(Random.Shared.Next(50, 101));
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[SideHub] Warning: couldn't lock {lockPath} ({ex.Message}), updating the registry without it");
                return null;
            }
        }
    }

    /// <summary>Keeps the unreadable registry as <c>instances.json.corrupt</c>, for the user to recover the other
    /// projects, and starts again from an empty list.</summary>
    private static List<InstanceEntry> SetAsideUnreadable(string registryPath)
    {
        var corruptPath = registryPath + ".corrupt";
        PrivateFiles.EnsureTrusted(corruptPath);
        File.Copy(registryPath, corruptPath, overwrite: true);
        PrivateFiles.RestrictFile(corruptPath);
        Console.WriteLine($"[SideHub] Warning: unreadable registry at {registryPath}, kept as {corruptPath}; " +
                          "other agents register again when they restart");
        return [];
    }

    /// <summary>Writes a temporary file next to the registry, then renames it over the registry: a reader gets the
    /// old or the new list, never a truncated one.</summary>
    private static void Save(string registryPath, List<InstanceEntry> entries)
    {
        var tempPath = $"{registryPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            PrivateFiles.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, registryPath, overwrite: true);
                    return;
                }
                // Windows refuses to replace a file another process is reading; readers are brief
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
                {
                    Thread.Sleep(ReadRetryDelay);
                }
            }
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public class InstanceEntry
{
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "";

    [JsonPropertyName("registeredAt")]
    public DateTime RegisteredAt { get; set; }
}
