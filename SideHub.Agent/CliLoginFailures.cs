namespace SideHub.Agent;

/// <summary>
/// What a CLI prints when it cannot authenticate with its login (expired, revoked, or never made), read on the last
/// lines of a run's terminal once it exited in failure (<see cref="TerminalText.LastLines"/>). The CLI is then reported
/// as not logged in at once (<see cref="VersionInfo.NoteLoggedOut"/>): its status command may still say it is, it only
/// reads the stored login.
/// <para>
/// Each phrase comes from the CLI's own text (its installed binary and a run without a valid login, see the fixtures of
/// the tests), never from memory, and a line has to start with it: a CLI working on login code prints "not logged in"
/// all day long. Only what a new login fixes: an API key that is refused is not one.
/// </para>
/// </summary>
public static class CliLoginFailures
{
    private static readonly Dictionary<string, string[]> Phrases = new(StringComparer.Ordinal)
    {
        // Claude Code 2.1.330, headless: "Failed to authenticate: OAuth session expired and could not be refreshed",
        // "… this login reached its time limit …", "… OAuth token revoked. …", "… your claude.ai login expired. …";
        // without any login: "Not logged in · Please run /login"
        ["claude"] = ["Failed to authenticate: ", "Not logged in · Please run /login"],
        // codex-cli 0.170.0, `codex exec` without a login
        ["codex"] = ["ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header"],
    };

    /// <summary>
    /// The CLI, among those the run started, that said it could not authenticate before the run exited in failure;
    /// null for none.
    /// </summary>
    public static string? Find(IEnumerable<string> providers, int exitCode, string? lastLines)
    {
        if (exitCode == 0 || string.IsNullOrEmpty(lastLines))
            return null;
        var lines = lastLines.Split('\n').Select(line => line.Trim()).ToList();
        return providers.FirstOrDefault(provider => Phrases.TryGetValue(provider, out var phrases)
            && lines.Any(line => phrases.Any(phrase => line.StartsWith(phrase, StringComparison.Ordinal))));
    }
}
