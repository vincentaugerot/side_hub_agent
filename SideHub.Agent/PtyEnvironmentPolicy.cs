using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// What the backend may change in a PTY it asks the agent to start: which <c>additionalEnv</c>
/// keys are merged into the shell environment, which scoped token (if any) the shell gets, and
/// which working directory is used. The backend is not trusted to override
/// the variables that decide what code the shell runs (PATH, LD_PRELOAD, rcfiles…).
/// </summary>
public static partial class PtyEnvironmentPolicy
{
    public const string AgentTokenKey = "SIDEHUB_AGENT_TOKEN";
    private const string SideHubPrefix = "SIDEHUB_";

    /// <summary>Tokens the backend scopes to one PTY: a run token (<c>run-*</c> PTYs) or an interactive
    /// terminal session token. The agent's own token (<c>sh_agent_</c>) never enters a PTY.</summary>
    private static readonly string[] ScopedTokenPrefixes = ["sh_run_", "sh_pty_"];

    /// <summary>Non-SIDEHUB_* keys the backend may set (run correlation for telemetry).</summary>
    private static readonly HashSet<string> AllowedExtraKeys = new(StringComparer.Ordinal)
    {
        "OTEL_RESOURCE_ATTRIBUTES",
        "OTEL_SERVICE_NAME",
    };

    /// <summary>SIDEHUB_* keys owned by the agent: they point at the API, the notify FIFO (and hold its
    /// secret), the wrappers and the bash rcfile, so overriding them would redirect the token or run code; or they
    /// state what the agent checked out (the commit a question run reads); or only <c>sidehub-cli launch</c> sets them
    /// (the CLI it started, which would otherwise make the terminal's first CLI look nested and go unannounced); or they
    /// name a file the CLI writes (a run's answer file, see <see cref="RunAnswerFiles"/>: any path would be overwritten); or
    /// they name the folder whose CLIs start trusted (the one the agent opened the terminal in).</summary>
    private static readonly HashSet<string> AgentOwnedKeys = new(StringComparer.Ordinal)
    {
        "SIDEHUB_PTY_SESSION_ID",
        "SIDEHUB_PTY_NOTIFY_FIFO",
        NotifyFifo.SecretVariable,
        "SIDEHUB_CLI_WRAPPERS",
        "SIDEHUB_BASHRC",
        "SIDEHUB_API_URL",
        "SIDEHUB_WORKSPACE_ID",
        "SIDEHUB_AGENT_ID",
        McpServerPolicy.EnvironmentKey,
        QuestionCheckout.CommitKey,
        "SIDEHUB_LAUNCHED_CLI",
        RunAnswerFiles.Variable,
        OpenedFolderVariable,
    };

    /// <summary>The folder the agent opened the terminal in, real path: <c>sidehub-cli launch</c> answers the trust
    /// question of a CLI started right there (the agent's repository), as the person may not see it (Conversation
    /// mode).</summary>
    public const string OpenedFolderVariable = "SIDEHUB_PTY_FOLDER";

    /// <summary>Variables a workspace secret may never replace: they decide what code the shell (or a CLI it starts)
    /// runs, where it looks for it, or who the shell is.</summary>
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "PWD", "OLDPWD", "TERM", "LANG", "TMPDIR", "TZ",
        "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONSTARTUP", "BASH_ENV", "ENV", "PROMPT_COMMAND", "IFS",
        "CLAUDECODE", "COMSPEC", "PATHEXT", "SYSTEMROOT", "USERPROFILE", "APPDATA",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvKeyPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex SecretKeyPattern();

    /// <summary>
    /// Keeps the <paramref name="additionalEnv"/> entries the backend may set for this PTY.
    /// <c>SIDEHUB_AGENT_TOKEN</c> is accepted only when it holds a scoped token (<c>sh_run_</c> or
    /// <c>sh_pty_</c>): it is the only credential the shell gets, and without it <c>sidehub-cli</c>
    /// is unavailable. The keys listed in <paramref name="secretKeys"/> (workspace secrets given to a
    /// run, e.g. <c>UBERSUGGEST_API_KEY</c>) pass too when they are UPPER_SNAKE_CASE and not a
    /// protected variable (<c>PATH</c>, <c>LD_PRELOAD</c>…) nor <c>SIDEHUB_*</c>. Rejected keys are
    /// returned for logging; their values never leave this method.
    /// </summary>
    public static Dictionary<string, string> FilterAdditionalEnv(
        IReadOnlyDictionary<string, string>? additionalEnv,
        out IReadOnlyList<string> rejectedKeys) =>
        FilterAdditionalEnv(additionalEnv, null, out rejectedKeys);

    /// <inheritdoc cref="FilterAdditionalEnv(IReadOnlyDictionary{string, string}?, out IReadOnlyList{string})"/>
    public static Dictionary<string, string> FilterAdditionalEnv(
        IReadOnlyDictionary<string, string>? additionalEnv,
        IReadOnlyCollection<string>? secretKeys,
        out IReadOnlyList<string> rejectedKeys)
    {
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<string>();
        if (additionalEnv is null)
        {
            rejectedKeys = rejected;
            return allowed;
        }

        foreach (var (key, value) in additionalEnv)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (IsAllowedEntry(key, value) || (secretKeys?.Contains(key) == true && IsAllowedSecretKey(key)))
                allowed[key] = value ?? string.Empty;
            else
                rejected.Add(key);
        }

        rejectedKeys = rejected;
        return allowed;
    }

    public static bool IsScopedToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && ScopedTokenPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsAllowedEntry(string key, string? value)
    {
        if (!EnvKeyPattern().IsMatch(key)) return false;
        if (key == AgentTokenKey) return IsScopedToken(value);
        if (AgentOwnedKeys.Contains(key)) return false;
        return key.StartsWith(SideHubPrefix, StringComparison.Ordinal) || AllowedExtraKeys.Contains(key);
    }

    private static bool IsAllowedSecretKey(string key) =>
        SecretKeyPattern().IsMatch(key)
        && !key.StartsWith(SideHubPrefix, StringComparison.Ordinal)
        && !ProtectedKeys.Contains(key);

    /// <summary>
    /// The directory a PTY starts in: <paramref name="requested"/> when it is the agent's working
    /// directory or one of its subfolders (relative paths resolve against it), with its symbolic
    /// links resolved, else the agent's working directory. Returns false when the request was refused.
    /// </summary>
    public static bool TryResolveWorkingDirectory(string agentWorkingDirectory, string? requested, out string resolved)
    {
        var root = Path.GetFullPath(agentWorkingDirectory);
        resolved = root;
        if (string.IsNullOrWhiteSpace(requested)) return true;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        }
        catch
        {
            return false;
        }

        string realCandidate;
        try
        {
            realCandidate = PathConfinement.RealPath(candidate);
            if (!PathConfinement.IsWithin(root, candidate)
                || !PathConfinement.IsWithin(PathConfinement.RealPath(root), realCandidate))
                return false;
        }
        catch (IOException)
        {
            return false;
        }

        // The resolved path, not the requested one: the PTY (and the skill files written into it)
        // must land where the check was made, not wherever a link points by the time it is used.
        resolved = realCandidate;
        return true;
    }
}
