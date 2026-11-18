using System.Runtime.Versioning;

namespace SideHub.Agent;

/// <summary>
/// Files only the agent's user may read: the configs, the logs, the PID file, the pending usage reports and the
/// agent tokens (in the user's configuration folder, see <see cref="AgentTokenStore"/>). Files are 0600 and folders
/// 0700, set at creation (no window where the umask applies) and re-applied to files left by older versions. No-op on Windows, where ACLs are inherited from the profile.
/// <para>Most live in the project's <c>.sidehub/</c> folder, which a commit can fill: a link committed there (say
/// <c>.sidehub/run/sidehub-agent.log -> ~/.bashrc</c>) would make the agent truncate, append to or chmod the target.
/// So no entry from <c>.sidehub/</c> down to the file may be a symbolic link or belong to another user; the operation
/// throws instead. Once those folders are checked and 0700, no one else can plant an entry in them.</para>
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode GroupOrOthers =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [UnsupportedOSPlatformGuard("windows")]
    private static bool Supported => !OperatingSystem.IsWindows();

    /// <summary>Creates the folder (and its missing parents) as 0700, or tightens it to 0700 when it exists.</summary>
    public static void CreateDirectory(string path)
    {
        EnsureTrusted(path);
        if (!Supported)
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        Restrict(path, PrivateDirectoryMode);
    }

    /// <summary>Writes the file, created as 0600; an existing file is tightened to 0600 first.</summary>
    public static void WriteAllText(string path, string contents)
    {
        RestrictFile(path);
        using var writer = new StreamWriter(path, Options(FileMode.Create));
        writer.Write(contents);
    }

    /// <summary>Opens the file for appending, created as 0600; an existing file is tightened to 0600 first.</summary>
    public static StreamWriter AppendText(string path)
    {
        RestrictFile(path);
        return new StreamWriter(path, Options(FileMode.Append));
    }

    /// <summary>Opens the file for exclusive use (a lock between processes), created as 0600; an existing file is
    /// tightened to 0600 first. Throws an <see cref="IOException"/> while another process holds it.</summary>
    public static FileStream OpenExclusive(string path)
    {
        RestrictFile(path);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (Supported) options.UnixCreateMode = PrivateFileMode;
        return new FileStream(path, options);
    }

    /// <summary>Sets an existing file to 0600. Missing files are ignored.</summary>
    public static void RestrictFile(string path)
    {
        EnsureTrusted(path);
        Restrict(path, PrivateFileMode);
    }

    /// <summary>Why the agent must not write to or chmod this path, or null when it may: the path, or one of its
    /// folders up to the enclosing <c>.sidehub/</c>, is a symbolic link or belongs to another user. Missing entries
    /// are fine (the agent creates them).</summary>
    public static string? UntrustedReason(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Above .sidehub/ is the project, which may legitimately be a link or belong to someone else.
        // Outside of a .sidehub/ folder, only the path itself is checked.
        var stopAt = fullPath;
        for (var dir = fullPath; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetFileName(dir) == ".sidehub")
            {
                stopAt = dir;
                break;
            }
        }

        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if (FileOwnership.Lstat(current) is { } status)
            {
                if (status.IsSymbolicLink)
                    return $"{current} is a symbolic link";
                if (status.Owner is { } owner && FileOwnership.CurrentUser is { } user && owner != user)
                    return $"{current} belongs to another user (uid {owner})";
            }
            if (current == stopAt)
                break;
        }
        return null;
    }

    /// <summary>Throws when <see cref="UntrustedReason"/> refuses the path.</summary>
    public static void EnsureTrusted(string path)
    {
        if (Supported && UntrustedReason(path) is { } reason)
            throw new IOException($"Refusing to use {path}: {reason}. Remove it (it may come from a commit).");
    }

    /// <summary>Whether the group or other users have any access to the file or folder.</summary>
    public static bool IsExposed(string path) =>
        Supported && Path.Exists(path) && (File.GetUnixFileMode(path) & GroupOrOthers) != 0;

    private static void Restrict(string path, UnixFileMode mode)
    {
        if (!Supported || !Path.Exists(path)) return;
        if (File.GetUnixFileMode(path) != mode)
            File.SetUnixFileMode(path, mode);
    }

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (Supported) options.UnixCreateMode = PrivateFileMode;
        return options;
    }
}
