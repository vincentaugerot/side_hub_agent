using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// <c>sidehub-cli cli-state claude &lt;working|waiting-input|idle&gt;</c> (a Claude Code hook: the hook's JSON on stdin
/// gives the session), <c>sidehub-cli cli-state agy &lt;working|idle&gt;</c> (an agy hook, same stdin with
/// <c>conversationId</c>) and <c>sidehub-cli cli-state codex &lt;event-json&gt;</c> (codex's <c>notify</c> program):
/// tells the agent hosting the terminal what the CLI is doing. Installed by <see cref="CliStateHooks"/>.
/// <c>sidehub-cli cli-state claude limit</c> (Claude's <c>StopFailure</c> hook) tells that the turn stopped on the plan's
/// usage limit, with what Claude said (doc 60). <c>sidehub-cli cli-state claude session</c> (Claude's <c>SessionStart</c>
/// hook on <c>resume</c> and <c>clear</c>) tells the session the CLI switched to: <c>/resume</c> and <c>/clear</c> leave
/// the one announced at launch.
/// <para>
/// It runs inside the CLI's turn, so it must never slow it down nor fail it: it writes nothing to stdout (Claude
/// would add it to the conversation on UserPromptSubmit) or stderr, gives up quickly, and always exits 0.
/// </para>
/// </summary>
public static class CliStateCommand
{
    public const string Name = "cli-state";

    public const string Working = "working";
    public const string WaitingInput = "waiting-input";
    public const string Idle = "idle";

    /// <summary>Not a state: the turn stopped on a usage limit (<see cref="LimitReport"/>).</summary>
    public const string Limit = "limit";

    /// <summary>Not a state: the CLI switched to another session (<see cref="SwitchedSession"/>).</summary>
    public const string Session = "session";

    /// <summary>Longest message passed on to the agent: it keeps <c>MaxMessageLength</c> (300) once masked.</summary>
    public const int MaxLimitMessageLength = 1000;

    private static readonly HashSet<string> States = [Working, WaitingInput, Idle];

    /// <summary>The hook JSON read from stdin at most (tool results can be large; session_id comes first).</summary>
    public const int MaxHookInputBytes = 256 * 1024;

    private static readonly TimeSpan StdinTimeout = TimeSpan.FromMilliseconds(500);

    public static int Run(string[] args)
    {
        try
        {
            if (args is ["claude", Session])
            {
                if (SwitchedSession(args, ReadHookInput) is { } session)
                    AgentNotifier.SessionStarted("claude", session, switched: true);
            }
            else if (LimitReport(args, ReadHookInput) is { } limit)
                AgentNotifier.Limit(limit.Provider, limit.CliSessionId, limit.Error, limit.Message);
            else if (Report(args, ReadHookInput) is { } report)
                AgentNotifier.State(report.Provider, report.State, report.CliSessionId, report.Prompt);
        }
        catch
        {
            // Never fail the CLI's turn over a status report.
        }
        return 0;
    }

    /// <param name="Prompt">What a waiting CLI asks (see <see cref="CliPrompt"/>), null when unknown.</param>
    public sealed record StateReport(string Provider, string State, string? CliSessionId, JsonObject? Prompt = null);

    /// <param name="Error">Claude's error category (<c>rate_limit</c>, <c>billing_error</c>).</param>
    /// <param name="Message">What Claude said (its last assistant message, else the error's details), cut to
    /// <see cref="MaxLimitMessageLength"/>; null when it said nothing.</param>
    public sealed record LimitReportData(string Provider, string? CliSessionId, string Error, string? Message);

    /// <summary>
    /// The limit to report for <c>claude limit</c> (a <c>StopFailure</c> hook), null for other arguments or another error
    /// (the hook's matcher already filters them, this guards a CLI that would not).
    /// </summary>
    public static LimitReportData? LimitReport(string[] args, Func<byte[]?> hookInput)
    {
        if (args is not ["claude", Limit])
            return null;
        var input = hookInput();
        string? error = null, message = null;
        if (input is { Length: > 0 })
        {
            try
            {
                using var doc = JsonDocument.Parse(input);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    error = Str(root, "error");
                    message = Str(root, "last_assistant_message") is { Length: > 0 } last ? last : Str(root, "error_details");
                }
            }
            catch (JsonException)
            {
                // Cut short (MaxHookInputBytes) or malformed: nothing tells it is a limit
            }
        }
        if (error is not ("rate_limit" or "billing_error"))
            return null;
        message = message?.Trim();
        if (message is { Length: > MaxLimitMessageLength })
            message = message[..MaxLimitMessageLength];
        return new LimitReportData("claude", SessionIdFromHookInput(input), error, string.IsNullOrEmpty(message) ? null : message);
    }

    /// <summary>
    /// The session claude switched to for <c>claude session</c> (a <c>SessionStart</c> hook: <c>/resume</c>, <c>/clear</c>,
    /// or a launch with <c>--resume</c> / <c>--continue</c>), null for other arguments or an input without a session.
    /// </summary>
    public static string? SwitchedSession(string[] args, Func<byte[]?> hookInput) =>
        args is ["claude", Session] ? SessionIdFromHookInput(hookInput()) : null;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>What to report for the arguments, or null when there is nothing to report.</summary>
    /// <param name="hookInput">Reads the hook's stdin (claude and agy), null when there is none.</param>
    public static StateReport? Report(string[] args, Func<byte[]?> hookInput)
    {
        if (args.Length != 2)
            return null;
        switch (args[0])
        {
            case "claude" when States.Contains(args[1]):
            {
                var input = hookInput();
                var prompt = args[1] == WaitingInput ? CliPrompt.FromHookInput(input) : null;
                return new StateReport("claude", args[1], SessionIdFromHookInput(input), prompt);
            }

            // agy's hooks give no waiting state: PreInvocation is working, Stop idle once nothing runs in the background.
            case "agy" when args[1] is Working or Idle:
            {
                var (conversationId, fullyIdle) = AntigravityHookInput(hookInput());
                return args[1] == Idle && fullyIdle == false ? null : new StateReport("agy", args[1], conversationId);
            }

            // codex runs its notify program with the event as the last argument; only turn ends are reported.
            case "codex":
                return CodexTurnComplete(args[1], out var threadId)
                    ? new StateReport("codex", Idle, threadId)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>The top-level <c>session_id</c> of a Claude Code hook input, when it is a UUID. The input may be cut
    /// short (<see cref="MaxHookInputBytes"/>): what was read is scanned as far as it goes.</summary>
    public static string? SessionIdFromHookInput(byte[]? input)
    {
        if (input is null || input.Length == 0)
            return null;
        try
        {
            var reader = new Utf8JsonReader(input, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return null;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;
                var isSessionId = reader.ValueTextEquals("session_id"u8);
                if (!reader.Read())
                    return null;
                if (isSessionId)
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { } id && CliLaunchPlan.IsSessionId(id)
                        ? id
                        : null;
                if ((reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) && !reader.TrySkip())
                    return null; // cut short inside a large value before session_id
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>conversationId</c> (when it is a UUID) and <c>fullyIdle</c> (Stop only) of an agy hook input.
    /// Its PreInvocation and Stop inputs are small: one that does not parse gives neither.</summary>
    public static (string? ConversationId, bool? FullyIdle) AntigravityHookInput(byte[]? input)
    {
        if (input is null || input.Length == 0)
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);
            var id = root.TryGetProperty("conversationId", out var conversation) && conversation.ValueKind == JsonValueKind.String
                && CliLaunchPlan.IsSessionId(conversation.GetString())
                    ? conversation.GetString()
                    : null;
            bool? fullyIdle = root.TryGetProperty("fullyIdle", out var idle) && idle.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? idle.GetBoolean()
                : null;
            return (id, fullyIdle);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Whether codex's notify argument is an <c>agent-turn-complete</c> event; its <c>thread-id</c> (the
    /// session) when it is a UUID.</summary>
    public static bool CodexTurnComplete(string json, out string? threadId)
    {
        threadId = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "agent-turn-complete")
                return false;
            if (root.TryGetProperty("thread-id", out var thread) && thread.ValueKind == JsonValueKind.String
                && CliLaunchPlan.IsSessionId(thread.GetString()))
                threadId = thread.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The hook input on stdin, bounded in size and time; null when stdin is a terminal (run by hand).</summary>
    private static byte[]? ReadHookInput()
    {
        if (!Console.IsInputRedirected)
            return null;
        var read = Task.Run(() =>
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[MaxHookInputBytes];
            var total = 0;
            int n;
            while (total < buffer.Length && (n = stdin.Read(buffer, total, buffer.Length - total)) > 0)
                total += n;
            return buffer[..total];
        });
        return read.Wait(StdinTimeout) ? read.Result : null;
    }
}
