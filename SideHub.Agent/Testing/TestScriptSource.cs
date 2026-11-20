using System.Text;

namespace SideHub.Agent.Testing;

/// <summary>What the agent read of a script: its text, or why it could not (<see cref="TestScriptSource"/>).</summary>
public sealed record TestScriptText(string? Content, string? Error);

/// <summary>
/// The source of the Playwright file of a scripted test (SideHub doc 73), read in the folder of the agent so that the
/// page of the test can show it: SideHub does not see the repository. Read only, and only a script: a path below the
/// folder, without following any link, with the extension of a Playwright file, of a readable size and made of text.
/// Who asks may already open a terminal on this agent; the rules keep the request to what it is for.
/// </summary>
public static class TestScriptSource
{
    /// <summary>Largest script sent: a spec is a few hundred lines.</summary>
    public const int MaxBytes = 512 * 1024;

    public const string NotAScript = "not-a-script";
    public const string NotFound = "not-found";
    public const string TooLarge = "too-large";
    public const string Unreadable = "unreadable";

    /// <summary>What a Playwright test is written in.</summary>
    private static readonly string[] Extensions = [".ts", ".js", ".mts", ".cts", ".mjs", ".cjs", ".tsx", ".jsx"];

    private static bool Exists(string directory, string path)
    {
        try { return File.Exists(Path.Combine(directory, path)) || Directory.Exists(Path.Combine(directory, path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    /// <param name="directory">The folder of the agent: the root of its repository.</param>
    /// <param name="file">The path of the script from that root, as the test names it.</param>
    public static TestScriptText Read(string directory, string? file)
    {
        var path = file?.Trim().Replace('\\', '/') ?? string.Empty;
        if (path.Length == 0 || Path.IsPathRooted(path) || !Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return new(null, NotAScript);
        try
        {
            using var stream = ConfinedFile.Open(directory, path, FileMode.Open, FileAccess.Read);
            if (stream.Length > MaxBytes)
                return new(null, TooLarge);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            // A file of text: a NUL byte is the sign of anything else.
            if (Array.IndexOf(bytes, (byte)0) >= 0)
                return new(null, NotAScript);
            return new(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'), null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null, NotFound);
        }
        // A link on the way, a folder in place of the file, a path that leaves the folder, a file that cannot be read;
        // or simply a folder of the path that does not exist.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, Exists(directory, path) ? Unreadable : NotFound);
        }
    }
}
