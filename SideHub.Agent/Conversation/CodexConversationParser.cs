using System.Text;
using System.Text.Json;
using static SideHub.Agent.Conversation.ConversationText;

namespace SideHub.Agent.Conversation;

/// <summary>
/// The conversation of a Codex session, from its rollout (<c>~/.codex/sessions/…/rollout-*.jsonl</c>): the items Codex
/// reports as done, <c>{"type":"event_msg","payload":{"type":"item_completed","item":{…}}}</c> (and <c>item_started</c>
/// for a call still running):
/// <list type="bullet">
/// <item><c>UserMessage</c>, <c>AgentMessage</c>: the messages;</item>
/// <item><c>CommandExecution</c> (with <c>parsed_cmd</c>: read, search, list_files, or a command), <c>FileChange</c>
/// (the files of a patch), <c>Extension</c> <c>web.search</c>: tool calls;</item>
/// <item><c>Reasoning</c>, sub-agent activity, hooks: left out; <c>ContextCompaction</c>: a notice.</item>
/// </list>
/// The <c>response_item</c> lines say the same for the model (with the injected instructions): not read. An item of a
/// type this parser does not know becomes an <see cref="ConversationKinds.Unknown"/> event.
/// </summary>
/// <param name="imageDirectories">Folders of the images pasted from SideHub (<c>.sidehub-images</c>): a reference to one of
/// their files in a message of the user becomes an image of it.</param>
public sealed class CodexConversationParser(string cwd, IReadOnlyCollection<string>? imageDirectories = null) : IConversationParser
{
    private readonly IReadOnlyCollection<string> _imageDirectories = imageDirectories ?? [];
    private readonly TurnTracker _turn = new();
    /// <summary>Output tokens of the whole session (<c>token_count</c>), and that total when the turn in progress started.</summary>
    private long _sessionTokens;
    private long _turnBase;

    private readonly HashSet<string> _unrecognized = new(StringComparer.Ordinal);
    private readonly List<string> _newUnrecognized = [];

    private static readonly HashSet<string> SkippedItems = new(StringComparer.Ordinal)
    {
        "Reasoning", "SubAgentActivity", "CollabAgentToolCall", "HookPrompt", "TodoList", "Plan",
    };

    public IReadOnlyList<ConversationEvent> Parse(string line)
    {
        if (line.Contains("\"token_count\"", StringComparison.Ordinal))
            return TokenCount(line);
        if (!line.Contains("\"item_completed\"", StringComparison.Ordinal) && !line.Contains("\"item_started\"", StringComparison.Ordinal))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Str(root, "type") != "event_msg" || Prop(root, "payload") is not { ValueKind: JsonValueKind.Object } payload)
                return [];
            var started = Str(payload, "type") switch
            {
                "item_completed" => false,
                "item_started" => true,
                _ => (bool?)null,
            };
            if (started is null || Prop(payload, "item") is not { ValueKind: JsonValueKind.Object } item
                || Str(item, "id") is not { } id || Str(item, "type") is not { } type)
                return [];
            var at = Timestamp(root);
            var events = new List<ConversationEvent>(2);
            if (Item(item, id, type, at, started.Value) is { } found)
            {
                events.Add(found);
                if (found.Kind == ConversationKinds.User)
                {
                    _turnBase = _sessionTokens;
                    events.Add(_turn.Start(found.Id, at));
                    return events;
                }
            }
            // The next message of the user is not part of the turn before it
            if (type != "UserMessage" && _turn.Touch(at) is { } turn) events.Add(turn);
            return events;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// <c>event_msg</c> <c>token_count</c>: the session's total output tokens so far (<c>info.total_token_usage</c>); the
    /// turn's are what it grew by since the turn started, so a count repeated by Codex is never counted twice.
    /// </summary>
    private List<ConversationEvent> TokenCount(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Str(root, "type") != "event_msg" || Prop(root, "payload") is not { ValueKind: JsonValueKind.Object } payload
                || Str(payload, "type") != "token_count" || Prop(payload, "info") is not { ValueKind: JsonValueKind.Object } info
                || Prop(info, "total_token_usage") is not { ValueKind: JsonValueKind.Object } total
                || Prop(total, "output_tokens") is not { ValueKind: JsonValueKind.Number } output || !output.TryGetInt64(out var tokens))
                return [];
            _sessionTokens = Math.Max(_sessionTokens, tokens);
            return _turn.SetTokens(_sessionTokens - _turnBase, Timestamp(root)) is { } turn ? [turn] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> TakeUnrecognized()
    {
        if (_newUnrecognized.Count == 0) return [];
        var taken = _newUnrecognized.ToList();
        _newUnrecognized.Clear();
        return taken;
    }

    private ConversationEvent? Item(JsonElement item, string id, string type, DateTime? at, bool started)
    {
        switch (type)
        {
            case "UserMessage":
                // A message is reported once, when it is sent
                return started ? null : Message(id, ConversationKinds.User, at, Prop(item, "content"));
            case "AgentMessage":
                return started ? null : Message(id, ConversationKinds.Assistant, at, Prop(item, "content"));
            case "CommandExecution":
                return Command(item, id, at, started);
            case "FileChange":
                return FileChange(item, id, at, started);
            case "Extension":
                return Extension(item, id, at, started);
            case "ContextCompaction":
                return started ? null : new ConversationEvent { Id = id, Kind = ConversationKinds.Note, At = at, Label = "compacted" };
            case var skipped when SkippedItems.Contains(skipped):
                return null;
            default:
                if (started) return null;
                if (_unrecognized.Add(type))
                    _newUnrecognized.Add(type);
                return new ConversationEvent { Id = id, Kind = ConversationKinds.Unknown, At = at, Label = type };
        }
    }

    private ConversationEvent? Message(string id, string kind, DateTime? at, JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Array } parts) return null;
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            var piece = Str(part, "type")?.ToLowerInvariant() switch
            {
                "text" or "input_text" or "output_text" => Str(part, "text"),
                "image" or "local_image" or "input_image" => "[image]",
                _ => null,
            };
            if (string.IsNullOrEmpty(piece)) continue;
            text.Append(text.Length > 0 ? "\n\n" : "").Append(piece);
        }
        var (rest, images) = kind == ConversationKinds.User ? TakeImages(text.ToString(), _imageDirectories) : (text.ToString(), null);
        if (string.IsNullOrWhiteSpace(rest) && images is null) return null;
        return new ConversationEvent
        {
            Id = id, Kind = kind, At = at, Text = string.IsNullOrWhiteSpace(rest) ? null : Head(rest.Trim(), MaxTextLength), Images = images,
        };
    }

    private ConversationEvent Command(JsonElement item, string id, DateTime? at, bool started)
    {
        // parsed_cmd: what Codex understood of the command ({"type":"read","name":…,"path":…}, "search", "list_files",
        // "unknown" with the command itself)
        string category = ToolCategories.Run;
        string? title = null;
        if (Prop(item, "parsed_cmd") is { ValueKind: JsonValueKind.Array } parsed && parsed.GetArrayLength() == 1)
        {
            var first = parsed[0];
            switch (Str(first, "type"))
            {
                case "read":
                    category = ToolCategories.Read;
                    title = Relative(Str(first, "path") ?? Str(first, "name"), cwd);
                    break;
                case "search":
                    category = ToolCategories.Search;
                    title = Str(first, "query") is { } query ? (Str(first, "path") is { } path ? $"{query}  ·  {Relative(path, cwd)}" : query) : null;
                    break;
                case "list_files":
                    category = ToolCategories.Search;
                    title = Relative(Str(first, "path"), cwd);
                    break;
            }
            title ??= FirstLine(Str(first, "cmd"));
        }
        title ??= FirstLine(CommandLine(Prop(item, "command")));

        var status = started ? ToolStatuses.Running : Str(item, "status") switch
        {
            "in_progress" => ToolStatuses.Running,
            "failed" or "declined" => ToolStatuses.Failed,
            _ => Prop(item, "exit_code") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var exit) && exit != 0
                ? ToolStatuses.Failed
                : ToolStatuses.Done,
        };
        return new ConversationEvent
        {
            Id = id, Kind = ConversationKinds.Tool, At = at, Tool = "CommandExecution", Category = category,
            Title = Head(title, MaxTitleLength), Status = status,
            Excerpt = started ? null : Head(Str(item, "aggregated_output"), MaxExcerptLength),
        };
    }

    /// <summary><c>["/bin/bash", "-lc", "the command"]</c> as the command typed, or the words joined.</summary>
    private static string? CommandLine(JsonElement? command)
    {
        if (command is { ValueKind: JsonValueKind.String } single) return single.GetString();
        if (command is not { ValueKind: JsonValueKind.Array } words) return null;
        var parts = words.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.String).Select(w => w.GetString()!).ToList();
        return parts.Count >= 3 && parts[^2] is "-lc" or "-c" ? parts[^1] : string.Join(' ', parts);
    }

    private ConversationEvent FileChange(JsonElement item, string id, DateTime? at, bool started)
    {
        var paths = new List<string>();
        var diff = new StringBuilder();
        if (Prop(item, "changes") is { ValueKind: JsonValueKind.Object } changes)
        {
            foreach (var change in changes.EnumerateObject())
            {
                paths.Add(Relative(change.Name, cwd) ?? change.Name);
                if (diff.Length >= MaxExcerptLength) continue;
                var piece = Str(change.Value, "unified_diff")
                    ?? (Str(change.Value, "type") == "add" ? Diff(null, Head(Str(change.Value, "content"), MaxExcerptLength)) : null)
                    ?? (Str(change.Value, "type") == "delete" ? $"- {Relative(change.Name, cwd)}" : null);
                if (piece is not null)
                    diff.Append(diff.Length > 0 ? "\n" : "").Append(piece);
            }
        }
        var failed = Str(item, "status") is "failed" or "declined";
        return new ConversationEvent
        {
            Id = id, Kind = ConversationKinds.Tool, At = at, Tool = "FileChange", Category = ToolCategories.Edit,
            Title = Head(string.Join(", ", paths), MaxTitleLength),
            Status = started ? ToolStatuses.Running : failed ? ToolStatuses.Failed : ToolStatuses.Done,
            Excerpt = Head(failed ? Str(item, "stderr") ?? Str(item, "stdout") : diff.Length > 0 ? diff.ToString() : null, MaxExcerptLength),
        };
    }

    private static ConversationEvent? Extension(JsonElement item, string id, DateTime? at, bool started)
    {
        var kind = Str(item, "kind");
        // A pause between two polls of a process: not a step of the conversation
        if (kind is null || kind.StartsWith("clock.", StringComparison.Ordinal))
            return null;
        string? title = kind == "web.search"
            ? Prop(item, "action") is { ValueKind: JsonValueKind.Object } action && Str(action, "url") is { } url ? url : Str(item, "query")
            : kind;
        return new ConversationEvent
        {
            Id = id, Kind = ConversationKinds.Tool, At = at, Tool = kind,
            Category = kind == "web.search" ? ToolCategories.Web : ToolCategories.Other,
            Title = Head(string.IsNullOrWhiteSpace(title) ? null : title, MaxTitleLength),
            Status = started ? ToolStatuses.Running : ToolStatuses.Done,
        };
    }
}
