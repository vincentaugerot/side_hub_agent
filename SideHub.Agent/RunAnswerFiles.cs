using System.Text;

namespace SideHub.Agent;

/// <summary>
/// The file where the CLI of a run writes its last answer, given to the PTY in <see cref="Variable"/>: <c>sidehub-cli
/// launch</c> passes it to <c>codex exec</c> as <c>--output-last-message</c>. <c>codex exec</c> announces no session, so
/// its transcript may not be found by the time a short run exits; this file needs no session. It lives in an
/// agent-owned 0700 folder (<c>.sidehub/run/answers/&lt;agent&gt;/</c>, see <see cref="PrivateFiles"/>) and is read
/// without following a link, so a link planted in its place cannot copy another file into the step's output.
/// </summary>
public sealed class RunAnswerFiles(string folder)
{
    /// <summary>Same name as <c>SideHub.Cli.Launch.LaunchCommand.RunAnswerFileVariable</c>.</summary>
    public const string Variable = "SIDEHUB_RUN_ANSWER_FILE";

    /// <summary>The only CLI that writes it.</summary>
    public const string Provider = "codex";

    /// <summary>Read up to this many bytes: well beyond what is sent (<see cref="CliMessageTracker.MaxLength"/>
    /// characters), so that the secrets are masked over all of the text that is kept.</summary>
    internal const int MaxBytes = 1024 * 1024;

    public string Folder { get; } = folder;

    /// <summary>The PTY's answer file, its folder created and a leftover of an earlier PTY deleted; null (with the
    /// reason) when the folder cannot be used: the run then falls back to the transcript.</summary>
    public string? Prepare(string ptySessionId, out string? problem)
    {
        problem = null;
        try
        {
            PrivateFiles.CreateDirectory(Folder);
            var path = PathFor(ptySessionId);
            File.Delete(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>The answer the CLI wrote, null when it wrote none (or only blanks). Throws <see cref="IOException"/>
    /// when the file is a link.</summary>
    public string? Read(string ptySessionId)
    {
        if (!Directory.Exists(Folder))
            return null;
        string text;
        try
        {
            using var stream = ConfinedFile.Open(Folder, FileName(ptySessionId), FileMode.Open, FileAccess.Read);
            var buffer = new byte[(int)Math.Min(stream.Length, MaxBytes)];
            stream.ReadExactly(buffer);
            text = Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Deletes the PTY's answer file; false when it is still there.</summary>
    public bool Delete(string ptySessionId)
    {
        try
        {
            File.Delete(PathFor(ptySessionId));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private string PathFor(string ptySessionId) => Path.Combine(Folder, FileName(ptySessionId));

    private static string FileName(string ptySessionId) =>
        NotifyFifo.IsValidPtySessionId(ptySessionId)
            ? $"{ptySessionId}.txt"
            : throw new ArgumentException("Invalid PTY session id.", nameof(ptySessionId));
}
