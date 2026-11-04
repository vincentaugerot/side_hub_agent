using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent.Models;

public class CommandExecuteMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.execute";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("shell")]
    public required string Shell { get; init; }
}

public class CommandOutputMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.output";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("stream")]
    public required string Stream { get; init; }

    [JsonPropertyName("data")]
    public required string Data { get; init; }
}

public class CommandCompletedMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.completed";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }
}

public class CommandFailedMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.failed";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }
}

public class CommandBusyMessage
{
    [JsonPropertyName("type")]
    public string Type => "command.busy";

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("reason")]
    public string Reason => "Agent is already executing a command";
}

public class IncomingMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("commandId")]
    public string? CommandId { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("shell")]
    public string? Shell { get; init; }

    [JsonPropertyName("input")]
    public string? Input { get; init; }

    [JsonPropertyName("columns")]
    public int? Columns { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>agent.update: the release to install ("1.0.91") and its tag ("v1.0.91").</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>agent.update: "when-idle" (default) or "now".</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    /// <summary><c>pty.conversation.watch</c>: whether someone looks at the PTY in Conversation mode (doc 69).</summary>
    [JsonPropertyName("on")]
    public bool? On { get; init; }

    // file.write fields
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("ptyPaste")]
    public string? PtyPaste { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }

    /// <summary>pty.start, review.round-start, review.diff.request: the work review (a GUID) the terminal's rounds belong
    /// to. On pty.start, the backend asks for the work done in this terminal to be reviewed.</summary>
    [JsonPropertyName("reviewId")]
    public string? ReviewId { get; init; }

    /// <summary>git.push.request: the commit the push was confirmed for (it must still be HEAD).</summary>
    [JsonPropertyName("head")]
    public string? Head { get; init; }

    /// <summary>review.diff.request: which round of the review.</summary>
    [JsonPropertyName("round")]
    public int? Round { get; init; }

    [JsonPropertyName("attachment")]
    public TerminalAttachmentPayload? Attachment { get; init; }

    // pty.start: optional dict of extra env vars to merge into the PTY environment
    // (workflow execution context, run token…). Filtered by PtyEnvironmentPolicy: only SIDEHUB_* and an allow-list.
    [JsonPropertyName("additionalEnv")]
    public Dictionary<string, string>? AdditionalEnv { get; init; }

    // pty.start: the additionalEnv keys whose values are secrets (workspace secrets): the agent replaces
    // those values with *** in pty.output and in the replayed history (SecretMasker).
    [JsonPropertyName("secretKeys")]
    public List<string>? SecretKeys { get; init; }

    // pty.start: the workspace MCP servers the run may use, values referencing secrets of additionalEnv as ${NAME}.
    // Checked by McpServerPolicy and handed to sidehub-cli launch, which gives them to the CLI.
    [JsonPropertyName("mcpServers")]
    public List<PtyMcpServer>? McpServers { get; init; }
}

/// <summary>One entry of <c>pty.start.mcpServers</c>: <c>command</c>/<c>args</c>/<c>env</c> for the <c>stdio</c>
/// transport, <c>url</c>/<c>headers</c> for <c>http</c>.</summary>
public class PtyMcpServer
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("revision")]
    public int? Revision { get; init; }

    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public List<string>? Args { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    // stdio: the secrets (of additionalEnv) the agent writes to a private file in the secretsFile format (dotenv, raw),
    // whose path replaces ${secrets_file} in args and env (McpSecretsFiles). Both absent for a server without a file.
    [JsonPropertyName("secrets")]
    public List<string>? Secrets { get; init; }

    [JsonPropertyName("secretsFile")]
    public string? SecretsFile { get; init; }
}

public class TerminalAttachmentPayload
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("base64Data")]
    public string? Base64Data { get; init; }
}

public class PtyOutputMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.output";

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }
}

public class PtyStartedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.started";

    [JsonPropertyName("shell")]
    public required string Shell { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }

    /// <summary>
    /// True when we reused an already-running PTY for this ptySessionId.
    /// False (default) when a fresh PTY was just spawned. The frontend uses
    /// this to decide whether to auto-resume a previously-captured CLI session.
    /// </summary>
    [JsonPropertyName("reattached")]
    public bool Reattached { get; init; }

    /// <summary>
    /// When the PTY was spawned (UTC), the same on every report of it: a reattach or the replay after a
    /// backend reconnect sends the original time, so the backend does not show the session as just started.
    /// Ignored by older backends.
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; init; }
}

/// <summary>
/// The agent will not start the PTY a <c>pty.start</c> asked for, and says why, so the backend fails the run at once
/// and the terminal tells the user, instead of waiting for a <c>pty.started</c> that never comes. Ignored by older
/// backends (they still time out).
/// </summary>
public class PtyStartRefusedMessage
{
    /// <summary>The updater is replacing the agent: anything started now would be cut within seconds.</summary>
    public const string AgentUpdating = "agent-updating";

    [JsonPropertyName("type")]
    public string Type => "pty.start-refused";

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public class PtyExitedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.exited";

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }

    /// <summary>
    /// For a run (<c>run-*</c>): the last lines the terminal showed, as plain text with secrets masked, so a failed
    /// run says why (CLI not logged in, missing command…). Null otherwise. Ignored by older backends.
    /// </summary>
    [JsonPropertyName("lastOutput")]
    public string? LastOutput { get; init; }
}

public class PtyHistoryMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.history";

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("bufferSize")]
    public required int BufferSize { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("ptySessionId")]
    public string? PtySessionId { get; init; }
}

/// <summary>
/// Emitted when a CLI session is started inside a PTY (e.g. the user typed
/// <c>claude</c> and our wrapper minted a UUID via <c>--session-id</c>). The
/// agent's FIFO reader posts this back to the backend so SideHub can link the
/// session to a task or display it in the UI.
/// </summary>
public class PtyCliSessionStartedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-session-started";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("cliSessionId")]
    public required string CliSessionId { get; init; }

    /// <summary>Re-sent after a backend reconnect for a CLI session already reported: the backend
    /// restores its state without treating it as a new conversation.</summary>
    [JsonPropertyName("replayed")]
    public bool Replayed { get; init; }
}

/// <summary>
/// What the CLI in a PTY is doing (<c>working</c>, <c>waiting-input</c> or <c>idle</c>, see <see cref="CliStates"/>),
/// reported by its hooks through <c>sidehub-cli cli-state</c>. Sent on each change and replayed after a backend
/// reconnect; nothing is sent when the CLI ends (the backend clears the state itself).
/// </summary>
public class PtyCliStateMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-state";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    /// <summary>The session the hook reported; null when it gave none.</summary>
    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>When the agent received the report (UTC).</summary>
    [JsonPropertyName("at")]
    public required DateTime At { get; init; }

    /// <summary>What the CLI asks while <c>waiting-input</c>, when its hook said (<see cref="PtyCliPrompt"/>).</summary>
    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PtyCliPrompt? Prompt { get; init; }
}

/// <summary>
/// What a waiting CLI asks, from its hook (<c>sidehub-cli cli-state</c>, see <c>CliPrompt</c> in SideHub.Cli), so the
/// phone can offer taps instead of arrow keys: a <c>question</c> (one single-choice question and its options) or a
/// <c>permission</c> (the tool and what it would act on). Untrusted like any FIFO line: bounded by
/// <see cref="FifoNotification"/>, shown as plain text.
/// </summary>
public sealed record PtyCliPrompt(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("question"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Question = null,
    [property: JsonPropertyName("header"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Header = null,
    [property: JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PtyCliPromptOption>? Options = null,
    [property: JsonPropertyName("tool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tool = null,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null)
{
    public const string QuestionKind = "question";
    public const string PermissionKind = "permission";

    // Same limits as sidehub-cli's CliPrompt, plus the ellipsis it adds when it cuts a text.
    public const int MaxQuestion = 301;
    public const int MaxDescription = 201;
    public const int MaxLabel = 121;
    public const int MaxHeader = 61;
    public const int MaxTool = 100;
    public const int MaxDetail = 501;
    public const int MaxOptions = 4;

    /// <summary>Same content (records compare their option lists by reference).</summary>
    public bool SameAs(PtyCliPrompt? other) =>
        other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
}

public sealed record PtyCliPromptOption(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);

/// <summary>
/// The conversation of the CLI in a PTY, read from its transcript while someone looks at the PTY in Conversation mode
/// (doc 69): a snapshot (<see cref="Reset"/>) when watching starts or the CLI session changes, then the events added or
/// changed. Secrets of the PTY masked. Never stored by the backend: relayed to the PTY's viewers only.
/// </summary>
public class PtyConversationMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.conversation";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    /// <summary>The CLI whose conversation it is; null when no CLI runs in the PTY.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    /// <summary>The events replace the thread shown (a snapshot), instead of being added to it.</summary>
    [JsonPropertyName("reset")]
    public bool Reset { get; init; }

    /// <summary>The conversation cannot be read (no CLI with Conversation mode, transcript not found): the terminal stays.</summary>
    [JsonPropertyName("unavailable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unavailable { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<SideHub.Agent.Conversation.ConversationEvent> Events { get; init; }
}

/// <summary>
/// The last answer of the CLI in a PTY (its last message with text, read from its transcript at the end of a turn), for
/// the phone's Reading mode. Secrets of the PTY masked, cut to <see cref="CliMessageTracker.MaxLength"/>. Replayed
/// after a backend reconnect.
/// </summary>
public class PtyCliMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-message";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>When the agent read it (UTC).</summary>
    [JsonPropertyName("at")]
    public required DateTime At { get; init; }
}

/// <summary>
/// The coding CLI of a PTY has exited (<c>sidehub-cli launch</c> wrote <c>cli-exited</c>): the terminal is a plain
/// shell again. The backend forgets the PTY's CLI (provider, state) so the UI stops treating it as a CLI.
/// </summary>
public class PtyCliExitedMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-exited";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>The session that ended; null when the launcher knew none (a new codex session).</summary>
    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }
}

/// <summary>
/// The CLI of a PTY stopped because its plan's usage window ran out (doc 60), see <see cref="CliLimitReport"/>. For a
/// run, sent before its <c>pty.exited</c>: the backend knows why the run stopped when the exit arrives.
/// </summary>
public class CliLimitMessage
{
    [JsonPropertyName("type")]
    public string Type => "cli.limit";

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    /// <summary>The run of a <c>run-*</c> PTY; null for a terminal.</summary>
    [JsonPropertyName("runId")]
    public Guid? RunId { get; init; }

    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    /// <summary>One of <see cref="CliLimitWindows"/>.</summary>
    [JsonPropertyName("window")]
    public required string Window { get; init; }

    /// <summary>When the window resets (UTC); null when the CLI did not say.</summary>
    [JsonPropertyName("resetsAt")]
    public DateTime? ResetsAt { get; init; }

    /// <summary>One of <see cref="CliLimitSources"/>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>What the CLI said, secrets masked, at most <see cref="CliLimitReport.MaxMessageLength"/> characters.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("at")]
    public required DateTime At { get; init; }
}

/// <summary>
/// Emitted when the agent observes that Claude has generated a conversation
/// title for a running CLI session (the "ai-title" line Claude writes to its
/// project JSONL). The backend forwards this over SSE so the UI can suggest it
/// as a tab label.
/// </summary>
public class PtyCliSessionTitledMessage
{
    [JsonPropertyName("type")]
    public string Type => "pty.cli-session-titled";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("cliSessionId")]
    public required string CliSessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}


/// <summary>
/// Raw token usage harvested for a SideHub-launched run (<c>run-*</c> PTY), per model.
/// No pricing: the backend stores it as-is. Re-sending for the same run and source replaces
/// the previous report, so the agent can report early (step end) and again at exit.
/// </summary>
public class RunUsageMessage
{
    [JsonPropertyName("type")]
    public string Type => "run.usage";

    [JsonPropertyName("runId")]
    public required Guid RunId { get; init; }

    /// <summary>Harvesting source (e.g. "claude-transcript"), or "unavailable" when usage cannot be measured.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("collectedAt")]
    public required DateTimeOffset CollectedAt { get; init; }

    [JsonPropertyName("models")]
    public required IReadOnlyList<ModelUsageReport> Models { get; init; }
}

/// <summary>
/// The final message of a question run (a read-only CLI answering a question about the repository from a
/// dedicated checkout), sent once when the run ends.
/// </summary>
public class RunAnswerMessage
{
    [JsonPropertyName("type")]
    public string Type => "run.answer";

    [JsonPropertyName("runId")]
    public required Guid RunId { get; init; }

    /// <summary>The CLI's last message (markdown), capped; null when none was found (see <see cref="Error"/>).</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>The commit the checkout was at: what the answer describes.</summary>
    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("commitDate")]
    public DateTimeOffset? CommitDate { get; init; }

    /// <summary>Why there is no text (e.g. "no-final-message"); null when there is.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public class ModelUsageReport
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cacheWriteTokens")]
    public long CacheWriteTokens { get; init; }

    [JsonPropertyName("reasoningTokens")]
    public long? ReasoningTokens { get; init; }

    [JsonPropertyName("requests")]
    public int Requests { get; init; }
}

/// <summary>
/// Raw token usage of a CLI session started in a terminal (not a <c>run-*</c> PTY: those report
/// <c>run.usage</c>), per model. A cumulative snapshot of the whole session: the backend replaces the
/// previous one for the same <c>cliSessionId</c>, never adds. <c>final</c> once the CLI has exited.
/// </summary>
public class CliSessionUsageMessage
{
    [JsonPropertyName("type")]
    public string Type => "cli-session.usage";

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("cliSessionId")]
    public required string CliSessionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>Harvesting source, as in <c>run.usage</c> (e.g. "claude-transcript").</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("collectedAt")]
    public required DateTimeOffset CollectedAt { get; init; }

    [JsonPropertyName("final")]
    public required bool Final { get; init; }

    [JsonPropertyName("models")]
    public required IReadOnlyList<ModelUsageReport> Models { get; init; }
}
