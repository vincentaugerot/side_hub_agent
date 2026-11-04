using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>
/// A line read from a PTY's notification FIFO (see <see cref="NotifyFifo"/>), validated. It must carry the PTY's
/// secret (<see cref="NotifyFifo.SecretVariable"/>), and any process of the terminal has it (it is in the PTY env),
/// so every field is untrusted: the CLI session id ends up in file
/// paths (<c>&lt;cliSessionId&gt;.jsonl</c>), the pid in <c>/proc</c> lookups, the cwd in rollout matching. Only the
/// shapes the wrappers and <c>sidehub-cli</c> write are accepted.
/// </summary>
public abstract record FifoNotification
{
    /// <summary>Longest line read from the FIFO, in characters: longer lines are discarded whole.</summary>
    public const int MaxLineLength = 8192;

    /// <summary>Longest accepted cwd, in characters (Linux PATH_MAX).</summary>
    public const int MaxCwdLength = 4096;

    /// <summary>CLI providers the wrappers announce, as they spell them.</summary>
    public static readonly IReadOnlySet<string> KnownProviders = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude", "codex", "gemini", "copilot", "agy",
    };

    /// <summary><c>sidehub-cli workflow step-complete|step-fail</c>: the step is over, the CLI may stay open.</summary>
    public sealed record RunStepEnded : FifoNotification;

    /// <summary><c>sidehub-cli task done</c>: the task of this terminal is finished (its work review round ends).</summary>
    public sealed record TaskDone : FifoNotification;

    /// <summary>The codex wrapper: <paramref name="Pid"/> is codex's process, null for wrappers that don't send it.</summary>
    public sealed record CliLaunched(string Provider, string Cwd, int? Pid) : FifoNotification;

    /// <summary>The claude wrapper: the session id it passed (or minted) with <c>--session-id</c> / <c>--resume</c>.
    /// <paramref name="Switched"/>: told by the CLI's own hook, once it moved to this session (claude's <c>/resume</c>,
    /// <c>/clear</c>), and ignored when it is the session the PTY already runs.</summary>
    public sealed record CliSessionStarted(string Provider, string CliSessionId, bool Switched = false) : FifoNotification;

    /// <summary><c>sidehub-cli launch</c>, once the CLI it started has exited: <paramref name="CliSessionId"/> is the session
    /// it announced, null when it knew none (a new codex session, found by the agent from the launch).</summary>
    public sealed record CliExited(string Provider, string? CliSessionId) : FifoNotification;

    /// <summary><c>sidehub-cli cli-state</c>, run by the CLI's own hooks: the CLI is now <paramref name="State"/> (one of
    /// <see cref="CliStates.All"/>). <paramref name="CliSessionId"/> is the session the hook reported, null when it gave none.
    /// <paramref name="Prompt"/>: what a waiting CLI asks, when its hook said (a malformed one is dropped, the state kept).</summary>
    public sealed record CliStateChanged(string Provider, string State, string? CliSessionId, PtyCliPrompt? Prompt = null) : FifoNotification;

    /// <summary><c>sidehub-cli cli-state claude limit</c>, run by Claude's <c>StopFailure</c> hook: the turn stopped on the
    /// plan's usage limit. <paramref name="Error"/>: <c>rate_limit</c> or <c>billing_error</c>; <paramref name="Message"/>: what
    /// Claude said, null when it said nothing.</summary>
    public sealed record CliLimitReached(string Provider, string? CliSessionId, string Error, string? Message) : FifoNotification;

    /// <summary>Longest limit message accepted, in characters (what <c>sidehub-cli</c> sends at most).</summary>
    public const int MaxLimitMessageLength = 1000;

    /// <summary>
    /// A CLI session id is accepted only as a canonical UUID (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>): hex digits
    /// and dashes, so it is safe in a file name.
    /// </summary>
    public static bool IsValidCliSessionId(string? cliSessionId) =>
        cliSessionId is { Length: 36 } && Guid.TryParseExact(cliSessionId, "D", out _);

    /// <summary>An absolute, normalized path (no <c>.</c>/<c>..</c> segment) without control characters.</summary>
    public static bool IsValidCwd(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd) || cwd.Length > MaxCwdLength || cwd.Any(char.IsControl)
            || !Path.IsPathFullyQualified(cwd))
            return false;
        try
        {
            return Path.GetFullPath(cwd) == cwd;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a FIFO line. Returns null with the reason in <paramref name="rejection"/> when the line lacks the PTY's
    /// <paramref name="secret"/>, is not one of the known events or a field is invalid; the reason never quotes the line.
    /// </summary>
    public static FifoNotification? Parse(string line, string secret, out string? rejection)
    {
        rejection = null;
        if (line.Length > MaxLineLength)
        {
            rejection = "line too long";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                rejection = "not a JSON object";
                return null;
            }
            if (!HasSecret(root, secret))
            {
                rejection = "missing or wrong secret";
                return null;
            }

            switch (String(root, "event"))
            {
                case "run-step-ended":
                    return new RunStepEnded();

                case "task-done":
                    return new TaskDone();

                case "cli-launched":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    var cwd = String(root, "cwd");
                    if (!IsValidCwd(cwd))
                    {
                        rejection = "invalid cwd";
                        return null;
                    }
                    int? pid = null;
                    if (root.TryGetProperty("pid", out var pidProperty))
                    {
                        if (pidProperty.ValueKind != JsonValueKind.Number || !pidProperty.TryGetInt32(out var p) || p <= 0)
                        {
                            rejection = "invalid pid";
                            return null;
                        }
                        pid = p;
                    }
                    return new CliLaunched(provider!, cwd!, pid);
                }

                case "cli-session-started":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    var cliSessionId = String(root, "cliSessionId");
                    if (!IsValidCliSessionId(cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    var switched = root.TryGetProperty("switched", out var s) && s.ValueKind == JsonValueKind.True;
                    return new CliSessionStarted(provider!, cliSessionId!, switched);
                }

                case "cli-exited":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    if (!TryOptionalCliSessionId(root, out var cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    return new CliExited(provider!, cliSessionId);
                }

                case "cli-state":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    var state = String(root, "state");
                    if (state is null || !CliStates.All.Contains(state))
                    {
                        rejection = "unknown state";
                        return null;
                    }
                    if (!TryOptionalCliSessionId(root, out var cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    var prompt = state == CliStates.WaitingInput && root.TryGetProperty("prompt", out var promptProperty)
                        ? ParsePrompt(promptProperty)
                        : null;
                    return new CliStateChanged(provider!, state, cliSessionId, prompt);
                }

                case "cli-limit":
                {
                    var provider = String(root, "provider");
                    if (!IsKnownProvider(provider, out rejection)) return null;
                    if (!TryOptionalCliSessionId(root, out var cliSessionId))
                    {
                        rejection = "invalid cliSessionId";
                        return null;
                    }
                    var error = String(root, "error");
                    if (error is not ("rate_limit" or "billing_error"))
                    {
                        rejection = "unknown error";
                        return null;
                    }
                    var message = Text(root, "message", MaxLimitMessageLength, required: false, out var messageOk);
                    if (!messageOk)
                    {
                        rejection = "invalid message";
                        return null;
                    }
                    return new CliLimitReached(provider!, cliSessionId, error, message);
                }

                default:
                    rejection = "unknown event";
                    return null;
            }
        }
        catch (JsonException)
        {
            rejection = "malformed JSON";
            return null;
        }
    }

    private static bool HasSecret(JsonElement root, string secret) =>
        secret.Length > 0
        && String(root, "secret") is { } sent
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(secret));

    /// <summary>An optional <c>cliSessionId</c>: absent or null is accepted (null), anything else must be a valid id.</summary>
    private static bool TryOptionalCliSessionId(JsonElement root, out string? cliSessionId)
    {
        cliSessionId = null;
        if (!root.TryGetProperty("cliSessionId", out var idProperty) || idProperty.ValueKind == JsonValueKind.Null)
            return true;
        cliSessionId = idProperty.ValueKind == JsonValueKind.String ? idProperty.GetString() : null;
        return IsValidCliSessionId(cliSessionId);
    }

    /// <summary>A prompt as <c>sidehub-cli</c> writes it, within its limits; null for anything else.</summary>
    public static PtyCliPrompt? ParsePrompt(JsonElement prompt)
    {
        if (prompt.ValueKind != JsonValueKind.Object)
            return null;
        switch (String(prompt, "kind"))
        {
            case PtyCliPrompt.QuestionKind:
            {
                var question = Text(prompt, "question", PtyCliPrompt.MaxQuestion, required: true, out var ok);
                var header = Text(prompt, "header", PtyCliPrompt.MaxHeader, required: false, out var headerOk);
                if (!ok || !headerOk
                    || !prompt.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array
                    || options.GetArrayLength() is < 1 or > PtyCliPrompt.MaxOptions)
                    return null;
                var list = new List<PtyCliPromptOption>();
                foreach (var option in options.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object)
                        return null;
                    var label = Text(option, "label", PtyCliPrompt.MaxLabel, required: true, out var labelOk);
                    var description = Text(option, "description", PtyCliPrompt.MaxDescription, required: false, out var descriptionOk);
                    if (!labelOk || !descriptionOk)
                        return null;
                    list.Add(new PtyCliPromptOption(label!, description));
                }
                return new PtyCliPrompt(PtyCliPrompt.QuestionKind, Question: question, Header: header, Options: list);
            }

            case PtyCliPrompt.PermissionKind:
            {
                var tool = Text(prompt, "tool", PtyCliPrompt.MaxTool, required: true, out var toolOk);
                var detail = Text(prompt, "detail", PtyCliPrompt.MaxDetail, required: false, out var detailOk);
                var description = Text(prompt, "description", PtyCliPrompt.MaxDescription, required: false, out var descriptionOk);
                if (!toolOk || !detailOk || !descriptionOk)
                    return null;
                return new PtyCliPrompt(PtyCliPrompt.PermissionKind, Tool: tool, Detail: detail, Description: description);
            }

            default:
                return null;
        }
    }

    /// <summary>An optional (or required) string field of at most <paramref name="max"/> characters, without control
    /// characters other than line breaks and tabs.</summary>
    private static string? Text(JsonElement obj, string name, int max, bool required, out bool ok)
    {
        ok = false;
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            ok = !required;
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
            || text.Length > max || (required && string.IsNullOrWhiteSpace(text))
            || text.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
            return null;
        ok = true;
        return text;
    }

    private static bool IsKnownProvider(string? provider, out string? rejection)
    {
        rejection = provider is not null && KnownProviders.Contains(provider) ? null : "unknown provider";
        return rejection is null;
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
