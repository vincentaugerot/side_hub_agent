using System.Text;
using System.Text.Json;
using static SideHub.Agent.Conversation.ConversationText;

namespace SideHub.Agent.Conversation;

/// <summary>
/// The conversation of a Claude Code session, from its JSONL (<c>~/.claude/projects/&lt;dir&gt;/&lt;session&gt;.jsonl</c>):
/// <list type="bullet">
/// <item><c>"type":"user"</c> lines: the user's messages (a string, or text and image parts), and the results of tool
/// calls (<c>tool_result</c> parts, matched to their call by <c>tool_use_id</c>);</item>
/// <item><c>"type":"assistant"</c> lines: one content part per line (<c>text</c>, <c>tool_use</c>, <c>thinking</c>);</item>
/// <item>every other line type is the CLI's metadata (titles, modes, attachments, requests): not part of the thread.</item>
/// </list>
/// Lines of sub-agents (<c>isSidechain</c>) and injected context (<c>isMeta</c>) are left out. A content part of a type
/// this parser does not know becomes an <see cref="ConversationKinds.Unknown"/> event.
/// </summary>
public sealed class ClaudeConversationParser(string cwd) : IConversationParser
{
    /// <summary>Calls seen, by <c>tool_use</c> id, to send them again with their result.</summary>
    private readonly Dictionary<string, ConversationEvent> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unrecognized = new(StringComparer.Ordinal);
    private readonly List<string> _newUnrecognized = [];

    private static readonly HashSet<string> SkippedParts = new(StringComparer.Ordinal)
    {
        "thinking", "redacted_thinking", "web_search_tool_result", "web_fetch_tool_result", "code_execution_tool_result",
        "mcp_tool_result", "tool_reference",
    };

    public IReadOnlyList<ConversationEvent> Parse(string line)
    {
        // Most lines are metadata: only user and assistant lines are parsed
        if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || True(root, "isSidechain") || True(root, "isMeta"))
                return [];
            var type = Str(root, "type");
            if (type is not ("user" or "assistant") || Prop(root, "message") is not { ValueKind: JsonValueKind.Object } message)
                return [];
            var id = Str(root, "uuid") ?? Str(message, "id");
            if (id is null) return [];
            var at = Timestamp(root);
            return type == "user" ? User(root, message, id, at) : Assistant(message, id, at);
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

    private List<ConversationEvent> User(JsonElement root, JsonElement message, string id, DateTime? at)
    {
        if (True(root, "isCompactSummary"))
            return [new ConversationEvent { Id = id, Kind = ConversationKinds.Note, At = at, Label = "compacted" }];

        var content = Prop(message, "content");
        if (content is { ValueKind: JsonValueKind.String } single)
            return UserText(id, at, single.GetString());
        if (content is not { ValueKind: JsonValueKind.Array } parts)
            return [];

        var events = new List<ConversationEvent>();
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            switch (Str(part, "type"))
            {
                case "text":
                    if (Str(part, "text") is { } piece)
                        text.Append(text.Length > 0 ? "\n\n" : "").Append(piece);
                    break;
                case "image":
                    text.Append(text.Length > 0 ? "\n\n" : "").Append("[image]");
                    break;
                case "tool_result":
                    if (ToolResult(part) is { } result)
                        events.Add(result);
                    break;
                case { } other:
                    events.Add(Unrecognized($"{id}:{events.Count}", at, $"user:{other}"));
                    break;
            }
        }
        if (text.Length > 0)
            events.InsertRange(0, UserText(id, at, text.ToString()));
        return events;
    }

    /// <summary>The user's text, or the notice it stands for: a slash command typed, a turn interrupted.</summary>
    private static List<ConversationEvent> UserText(string id, DateTime? at, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var trimmed = text.Trim();
        if (trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
            return [new ConversationEvent { Id = id, Kind = ConversationKinds.Note, At = at, Label = "interrupted" }];
        if (trimmed.StartsWith("<command-name>", StringComparison.Ordinal) || trimmed.StartsWith("<command-message>", StringComparison.Ordinal))
        {
            var command = Tag(trimmed, "command-name");
            if (command is null) return [];
            var args = Tag(trimmed, "command-args");
            return [new ConversationEvent { Id = id, Kind = ConversationKinds.User, At = at, Text = string.IsNullOrWhiteSpace(args) ? command : $"{command} {args}" }];
        }
        if (trimmed.StartsWith("<bash-input>", StringComparison.Ordinal))
            return Tag(trimmed, "bash-input") is { } bash
                ? [new ConversationEvent { Id = id, Kind = ConversationKinds.User, At = at, Text = $"! {bash}" }]
                : [];
        // Output of a local command (/cost, ! ls) and other tags of the CLI: the terminal shows them
        if (trimmed.StartsWith("<local-command-", StringComparison.Ordinal) || trimmed.StartsWith("<bash-std", StringComparison.Ordinal)
            || trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal))
            return [];
        return [new ConversationEvent { Id = id, Kind = ConversationKinds.User, At = at, Text = Head(text, MaxTextLength) }];
    }

    private static string? Tag(string text, string name)
    {
        var open = $"<{name}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end].Trim();
    }

    private ConversationEvent? ToolResult(JsonElement part)
    {
        if (Str(part, "tool_use_id") is not { } callId || !_calls.TryGetValue(callId, out var call))
            return null;
        var failed = True(part, "is_error");
        var output = ResultText(Prop(part, "content"));
        var updated = call with
        {
            Status = failed ? ToolStatuses.Failed : ToolStatuses.Done,
            // An edit keeps its diff; any other tool shows the start of what it returned, a failure always its error
            Excerpt = Head(failed || call.Category != ToolCategories.Edit || call.Excerpt is null ? output ?? call.Excerpt : call.Excerpt, MaxExcerptLength),
        };
        _calls[callId] = updated;
        return updated;
    }

    private List<ConversationEvent> Assistant(JsonElement message, string id, DateTime? at)
    {
        if (Prop(message, "content") is not { ValueKind: JsonValueKind.Array } parts)
            return [];
        var events = new List<ConversationEvent>();
        var index = 0;
        foreach (var part in parts.EnumerateArray())
        {
            var partId = index++ == 0 ? id : $"{id}:{index - 1}";
            switch (Str(part, "type"))
            {
                case "text":
                    if (Str(part, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                        events.Add(new ConversationEvent { Id = partId, Kind = ConversationKinds.Assistant, At = at, Text = Head(text, MaxTextLength) });
                    break;
                case "tool_use":
                case "server_tool_use":
                    if (ToolCall(part, at) is { } call)
                        events.Add(call);
                    break;
                case { } type when SkippedParts.Contains(type):
                    break;
                case { } other:
                    events.Add(Unrecognized(partId, at, $"assistant:{other}"));
                    break;
            }
        }
        return events;
    }

    private ConversationEvent? ToolCall(JsonElement part, DateTime? at)
    {
        if (Str(part, "id") is not { } callId || Str(part, "name") is not { } name)
            return null;
        var input = Prop(part, "input") ?? default;
        var (category, title, excerpt) = Describe(name, input);
        var call = new ConversationEvent
        {
            Id = callId, Kind = ConversationKinds.Tool, At = at, Tool = name, Category = category,
            Title = Head(title, MaxTitleLength), Excerpt = Head(excerpt, MaxExcerptLength),
            // A server tool (web search) runs on Anthropic's side: its result comes in the same message
            Status = Str(part, "type") == "server_tool_use" ? ToolStatuses.Done : ToolStatuses.Running,
        };
        // A call written again (same id) keeps the result it already has
        if (_calls.TryGetValue(callId, out var known) && known.Status != ToolStatuses.Running)
            call = call with { Status = known.Status, Excerpt = known.Excerpt };
        _calls[callId] = call;
        return call;
    }

    /// <summary>What a Claude Code tool does, what it works on, and for an edit its diff.</summary>
    private (string Category, string? Title, string? Excerpt) Describe(string name, JsonElement input)
    {
        string? Path() => Relative(Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path"), cwd);
        switch (name)
        {
            case "Read":
                return (ToolCategories.Read, Path(), null);
            case "Edit":
                return (ToolCategories.Edit, Path(), Diff(Str(input, "old_string"), Str(input, "new_string")));
            case "MultiEdit":
            {
                var diff = new StringBuilder();
                if (Prop(input, "edits") is { ValueKind: JsonValueKind.Array } edits)
                    foreach (var edit in edits.EnumerateArray())
                        if (Diff(Str(edit, "old_string"), Str(edit, "new_string")) is { } one && diff.Length < MaxExcerptLength)
                            diff.Append(diff.Length > 0 ? "\n" : "").Append(one);
                return (ToolCategories.Edit, Path(), diff.Length > 0 ? diff.ToString() : null);
            }
            case "Write":
                return (ToolCategories.Edit, Path(), Diff(null, Head(Str(input, "content"), MaxExcerptLength)));
            case "NotebookEdit":
                return (ToolCategories.Edit, Path(), Diff(null, Head(Str(input, "new_source"), MaxExcerptLength)));
            case "Grep":
                return (ToolCategories.Search, Join(Str(input, "pattern"), Path()), null);
            case "Glob":
                return (ToolCategories.Search, Join(Str(input, "pattern"), Path()), null);
            case "LS":
                return (ToolCategories.Search, Path(), null);
            case "Bash":
            case "PowerShell":
                return (ToolCategories.Run, FirstLine(Str(input, "command")), null);
            case "BashOutput":
            case "KillShell":
            case "KillBash":
                return (ToolCategories.Run, Str(input, "bash_id") ?? Str(input, "shell_id"), null);
            case "WebFetch":
                return (ToolCategories.Web, Str(input, "url"), null);
            case "WebSearch":
            case "web_search":
                return (ToolCategories.Web, Str(input, "query"), null);
            case "Task":
            case "Agent":
                return (ToolCategories.Task, FirstLine(Str(input, "description") ?? Str(input, "prompt")), null);
            case "TodoWrite":
                return (ToolCategories.Task, null, null);
            default:
                return (ToolCategories.Other, FirstLine(Str(input, "description") ?? Str(input, "command") ?? Path()), null);
        }
    }

    private static string? Join(string? pattern, string? path) =>
        pattern is null ? path : path is null ? pattern : $"{pattern}  ·  {path}";

    private ConversationEvent Unrecognized(string id, DateTime? at, string label)
    {
        if (_unrecognized.Add(label))
            _newUnrecognized.Add(label);
        return new ConversationEvent { Id = id, Kind = ConversationKinds.Unknown, At = at, Label = label };
    }
}
