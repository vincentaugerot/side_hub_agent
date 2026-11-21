namespace SideHub.Agent.Tests;

/// <summary>
/// A run whose CLI could not authenticate with its login says which CLI, from the CLI's own text at the end of its
/// terminal, and that CLI stays reported as not logged in while its status command still says it is.
/// </summary>
public class CliLoginFailuresTests
{
    private static readonly DateTime Noon = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The lines of a fixture of <c>Fixtures/cli-logins</c>, comments left out.</summary>
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-logins", name))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

    [Theory]
    [InlineData("claude", "claude-2.1.330.txt")]
    [InlineData("codex", "codex-0.170.0.txt")]
    public void Each_fixture_line_is_found_at_the_end_of_a_failed_run(string provider, string fixture)
    {
        foreach (var line in Fixture(fixture))
            Assert.Equal(provider, CliLoginFailures.Find([provider], 1, $"$ {provider} exec \"fix it\"\n  {line}  \n"));
    }

    [Fact]
    public void The_last_lines_of_the_runs_that_failed_in_production_name_claude()
    {
        // As SideHub kept them (TerminalText.LastLines): the typed command, its redrawn end, then the CLI's error
        var lastOutput = """
            sidehub-cli launch --prompt-env claude -p --session-id 8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14 --restricted --tools=Read,Grep,Glob --allowedTools=Read,Grep,Glob --permission-mode dontAsk --strict-mcp-config; exit
            onfig; exit
            Failed to authenticate: OAuth session expired and could not be refreshed
            """;

        Assert.Equal("claude", CliLoginFailures.Find(["claude"], 1, lastOutput));
    }

    [Fact]
    public void A_run_that_succeeded_or_printed_nothing_lost_no_login()
    {
        var line = Fixture("claude-2.1.330.txt")[0];

        Assert.Null(CliLoginFailures.Find(["claude"], 0, line));
        Assert.Null(CliLoginFailures.Find(["claude"], 1, null));
        Assert.Null(CliLoginFailures.Find(["claude"], 1, ""));
    }

    [Theory]
    // Another CLI's text, a CLI the run did not start, a CLI without a known phrase
    [InlineData("codex", "Failed to authenticate: OAuth session expired and could not be refreshed")]
    [InlineData("copilot", "Not logged in · Please run /login")]
    // Code or an answer about logins: the phrase is not at the start of the line
    [InlineData("claude", "throw new Error(\"Failed to authenticate: \" + reason);")]
    [InlineData("claude", "The CLI prints Not logged in · Please run /login when it has no login.")]
    // What a new login does not fix
    [InlineData("claude", "Invalid API key · Fix external API key")]
    [InlineData("claude", "API Error: 500 Internal server error")]
    public void Another_clis_text_code_about_logins_and_other_errors_are_no_lost_login(string provider, string output) =>
        Assert.Null(CliLoginFailures.Find([provider], 1, output));

    [Fact]
    public void Among_the_clis_a_run_started_the_one_that_said_it_is_named()
    {
        var line = Fixture("codex-0.170.0.txt")[0];

        Assert.Equal("codex", CliLoginFailures.Find(["claude", "codex"], 1, line));
        Assert.Null(CliLoginFailures.Find([], 1, line));
    }

    // ---- what is reported after a probe ----

    private static Dictionary<string, DateTime> Found(params string[] clis) => clis.ToDictionary(cli => cli, _ => Noon);

    [Fact]
    public void A_cli_a_run_found_logged_out_stays_so_while_its_status_command_says_it_is_logged_in()
    {
        var found = Found("claude");

        var auth = VersionInfo.AfterProbe(new Dictionary<string, bool> { ["claude"] = true, ["codex"] = true }, found,
            statusTrusted: false, Noon.AddMinutes(5));

        Assert.False(auth["claude"]);
        Assert.True(auth["codex"]);
        Assert.Contains("claude", found.Keys);
    }

    [Fact]
    public void A_cli_whose_state_is_unknown_stays_logged_out_too()
    {
        var auth = VersionInfo.AfterProbe(new Dictionary<string, bool>(), Found("agy"), statusTrusted: false, Noon.AddMinutes(5));

        Assert.False(auth["agy"]);
    }

    [Fact]
    public void Once_its_status_command_says_logged_out_it_is_followed_again()
    {
        var found = Found("claude");

        // claude cleared the login it could not renew: its status command now says so
        var loggedOut = VersionInfo.AfterProbe(new Dictionary<string, bool> { ["claude"] = false }, found, statusTrusted: false, Noon.AddMinutes(1));
        // The user logged in again on the machine: seen by the next probe, whatever started it
        var loggedIn = VersionInfo.AfterProbe(new Dictionary<string, bool> { ["claude"] = true }, found, statusTrusted: false, Noon.AddHours(2));

        Assert.False(loggedOut["claude"]);
        Assert.True(loggedIn["claude"]);
        Assert.Empty(found);
    }

    [Fact]
    public void A_terminal_of_the_user_that_closed_makes_the_status_command_believed_again()
    {
        var found = Found("claude");

        var auth = VersionInfo.AfterProbe(new Dictionary<string, bool> { ["claude"] = true }, found, statusTrusted: true, Noon.AddMinutes(5));

        Assert.True(auth["claude"]);
        Assert.Empty(found);
    }

    [Fact]
    public void Unless_the_run_found_it_after_that_terminal_closed()
    {
        var found = Found("claude");

        var auth = VersionInfo.AfterProbe(new Dictionary<string, bool> { ["claude"] = true }, found, statusTrusted: true, Noon.AddSeconds(-3));

        Assert.False(auth["claude"]);
        Assert.Contains("claude", found.Keys);
    }
}
