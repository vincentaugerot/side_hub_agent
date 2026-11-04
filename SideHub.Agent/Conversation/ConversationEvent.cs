using System.Text.Json.Serialization;

namespace SideHub.Agent.Conversation;

/// <summary>
/// One step of a CLI's conversation, as the Conversation mode of a terminal shows it (doc 69): a message of the user, a
/// text of the assistant, a tool call with its state, or a line the reader does not know (shown as « see the terminal »).
/// Read from the CLI's transcript, never from its terminal. A tool call is sent again, with the same id, once its result
/// is written.
/// </summary>
public sealed record ConversationEvent
{
    /// <summary>Stable within the session: a newer event with the same id replaces the older one.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>One of <see cref="ConversationKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>When the CLI wrote it (UTC), if the line says.</summary>
    [JsonPropertyName("at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? At { get; init; }

    /// <summary>Message of the user or text of the assistant (markdown).</summary>
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>The CLI's own name of the tool (<c>Bash</c>, <c>Edit</c>, <c>CommandExecution</c>…).</summary>
    [JsonPropertyName("tool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; init; }

    /// <summary>One of <see cref="ToolCategories"/>: what the front says the tool does (reads, edits, runs…).</summary>
    [JsonPropertyName("category"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    /// <summary>What the tool works on: a path, a command, a pattern, a URL.</summary>
    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>One of <see cref="ToolStatuses"/>.</summary>
    [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    /// <summary>The start of the tool's result (or of its change), as plain text.</summary>
    [JsonPropertyName("excerpt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Excerpt { get; init; }

    /// <summary>For an <see cref="ConversationKinds.Unknown"/> event: the type of what was not recognized.</summary>
    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    /// <summary>The length of its texts, to keep a snapshot within <see cref="ConversationFeed.MaxSnapshotChars"/>.</summary>
    [JsonIgnore]
    public int Size => (Text?.Length ?? 0) + (Title?.Length ?? 0) + (Excerpt?.Length ?? 0) + 64;
}

public static class ConversationKinds
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
    /// <summary>A notice of the CLI in the thread: a turn interrupted, the conversation compacted (<c>label</c>).</summary>
    public const string Note = "note";
    public const string Unknown = "unknown";
}

public static class ToolCategories
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Search = "search";
    public const string Run = "run";
    public const string Web = "web";
    public const string Task = "task";
    public const string Other = "other";
}

public static class ToolStatuses
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>Turns the lines of one CLI's transcript into <see cref="ConversationEvent"/>s, in order.</summary>
public interface IConversationParser
{
    /// <summary>
    /// The events of one transcript line: new ones, or a newer version of one already returned (same id, a tool call
    /// once its result is written). Empty for a line outside the conversation (metadata, reasoning, usage). Never throws:
    /// a line that cannot be read is skipped.
    /// </summary>
    IReadOnlyList<ConversationEvent> Parse(string line);

    /// <summary>Types met but not recognized since the last call, each once per session: logged by the agent.</summary>
    IReadOnlyList<string> TakeUnrecognized();
}
