using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SideHub.Agent.Conversation.ConversationText;

namespace SideHub.Agent.Conversation;

/// <summary>
/// The conversation of a Claude Code session, from its JSONL (<c>~/.claude/projects/&lt;dir&gt;/&lt;session&gt;.jsonl</c>):
/// <list type="bullet">
/// <item><c>"type":"user"</c> lines: the user's messages (a string, or text and image parts), and the results of tool
/// calls (<c>tool_result</c> parts, matched to their call by <c>tool_use_id</c>);</item>
/// <item><c>"type":"assistant"</c> lines: one content part per line (<c>text</c>, <c>tool_use</c>, <c>thinking</c>: left out, but for the narration the terminal shows as text, see <see cref="IsNarration"/>);</item>
/// <item><c>"type":"attachment"</c> lines of a <c>queued_command</c>: a message the user typed while Claude worked, or the
/// notice of a background task;</item>
/// <item>a message of another Claude session of the machine (<c>&lt;cross-session-message&gt;</c>, <see cref="PeerMessage"/>):
/// a message of the user labelled <c>peer</c>, with the name of the session that sent it;</item>
/// <item>every other line type is the CLI's metadata (titles, modes, attachments, requests): not part of the thread.</item>
/// </list>
/// Lines of sub-agents (<c>isSidechain</c>, unless <paramref name="sidechain"/>: the transcript of a sub-agent, see
/// <see cref="ClaudeSubagents"/>) and injected context (<c>isMeta</c>) are left out. A content part of a type
/// this parser does not know becomes an <see cref="ConversationKinds.Unknown"/> event.
/// </summary>
/// <param name="imageDirectories">Folders of the images pasted from SideHub (<c>.sidehub-images</c>): a reference to one of
/// their files becomes an image of the message.</param>
public sealed class ClaudeConversationParser(string cwd, IReadOnlyCollection<string>? imageDirectories = null, bool sidechain = false)
    : IConversationParser
{
    private readonly IReadOnlyCollection<string> _imageDirectories = imageDirectories ?? [];
    private readonly TurnTracker _turn = new();
    /// <summary>Background tasks launched, by the CLI's task id (what a monitor's notices name), to their call.</summary>
    private readonly Dictionary<string, string> _backgroundTasks = new(StringComparer.Ordinal);

    /// <summary>Calls seen, by <c>tool_use</c> id, to send them again with their result.</summary>
    private readonly Dictionary<string, ConversationEvent> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unrecognized = new(StringComparer.Ordinal);
    private readonly List<string> _newUnrecognized = [];
    /// <summary>Image files read by <c>Read</c> calls, by call id: the file of the image its result holds.</summary>
    private readonly Dictionary<string, string> _readPaths = new(StringComparer.Ordinal);

    public ToolImages Images { get; } = new();

    private static readonly HashSet<string> SkippedParts = new(StringComparer.Ordinal)
    {
        "thinking", "redacted_thinking", "web_search_tool_result", "web_fetch_tool_result", "code_execution_tool_result",
        "mcp_tool_result", "tool_reference",
    };

    public IReadOnlyList<ConversationEvent> Parse(string line)
    {
        // A background task that ends while Claude works is only queued (then absorbed into the turn): its call ends here
        if ((line.Contains("\"queue-operation\"", StringComparison.Ordinal) || line.Contains("\"queued_command\"", StringComparison.Ordinal))
            && line.Contains("task-notification", StringComparison.Ordinal))
            return QueuedNotification(line);
        // A message the user typed while Claude works, absorbed into the turn: it never comes as a user line
        if (line.Contains("\"queued_command\"", StringComparison.Ordinal))
            return QueuedMessage(line);
        // Most lines are metadata: only user and assistant lines are parsed
        if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            // A message of another Claude session is injected context (isMeta), written for the thread all the same
            if (root.ValueKind != JsonValueKind.Object || (!sidechain && True(root, "isSidechain")) || (True(root, "isMeta") && !FromPeer(root)))
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
            return StartTurn(UserText(id, at, single.GetString()), at);
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
        return StartTurn(events, at);
    }

    /// <summary>A message of the user, or a background task's notice, starts a turn: its figures follow it.</summary>
    private List<ConversationEvent> StartTurn(List<ConversationEvent> events, DateTime? at)
    {
        var start = events.FirstOrDefault(e => e.Kind == ConversationKinds.User || e is { Kind: ConversationKinds.Note, Label: "task" });
        if (start is not null) events.Add(_turn.Start(start.Id, at));
        return events;
    }

    /// <summary>The user's text, or the notice it stands for: a slash command typed, a turn interrupted, a background task ended.</summary>
    private List<ConversationEvent> UserText(string id, DateTime? at, string? text)
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
        if (trimmed.StartsWith("<task-notification>", StringComparison.Ordinal))
            return TaskNotifications(id, at, trimmed);
        if (PeerMessage.Match(trimmed) is { Success: true } peer)
            return string.IsNullOrWhiteSpace(peer.Groups["body"].Value) ? [] : [new ConversationEvent
            {
                Id = id, Kind = ConversationKinds.User, At = at, Label = "peer",
                From = PeerName.Match(peer.Groups["attrs"].Value) is { Success: true } name && !string.IsNullOrWhiteSpace(name.Groups[1].Value)
                    ? Head(WebUtility.HtmlDecode(name.Groups[1].Value).Trim(), MaxTitleLength)
                    : null,
                Text = Head(peer.Groups["body"].Value.Trim(), MaxTextLength),
            }];
        var (rest, images) = TakeImages(Unpasted(text), _imageDirectories);
        if (string.IsNullOrWhiteSpace(rest) && images is null) return [];
        return [new ConversationEvent
        {
            Id = id, Kind = ConversationKinds.User, At = at, Text = string.IsNullOrWhiteSpace(rest) ? null : Head(rest.Trim(), MaxTextLength), Images = images,
        }];
    }

    /// <summary>
    /// The result of a launch in the background, from its first words (any output may quote them further on), with the
    /// task id its notices give: a command (« Command running in background with ID: b1x »), a monitor (« Monitor started
    /// (task b2y, »), a sub-agent (« Async agent launched successfully. … » then « agentId: a3z » on the next line).
    /// </summary>
    private static readonly Regex BackgroundTask = new(
        @"\A(?:Command running in background with ID: (?<id>[A-Za-z0-9_-]+)|Monitor started \(task (?<id>[A-Za-z0-9_-]+)|Async agent launched successfully\.[^\n]*\nagentId: (?<id>[A-Za-z0-9_-]+))",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A message sent by another Claude session on this machine (<c>SendMessage</c> to a local session): one
    /// <c>&lt;cross-session-message from="…" from-name="…" from-mode="…"&gt;</c> block, alone when the running turn takes it
    /// in (a <c>queued_command</c>), after « Another Claude session sent a message: » when it starts a turn (an
    /// <c>isMeta</c> user line whose <c>origin.kind</c> is <c>peer</c>).
    /// </summary>
    private static readonly Regex PeerMessage = new(
        @"\A(?:[^\n<]*\r?\n)?<cross-session-message(?<attrs>[^>]*)>(?<body>.*?)</cross-session-message>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex PeerName = new(@"\sfrom-name=""([^""]*)""", RegexOptions.CultureInvariant);

    private static bool FromPeer(JsonElement root) =>
        Prop(root, "origin") is { ValueKind: JsonValueKind.Object } origin && Str(origin, "kind") == "peer";

    private static readonly Regex PastedOpen = new(@"<pasted_content id=""[^""\r\n]*"">\r?\n?", RegexOptions.CultureInvariant);
    private static readonly Regex PastedClose = new(@"\r?\n?</pasted_content(?: id=""[^""\r\n]*"")?>", RegexOptions.CultureInvariant);
    private static readonly Regex PastedEscaped = new(@"<(/?)\\(/?)pasted_content", RegexOptions.CultureInvariant);

    /// <summary>
    /// The message as the user wrote it: Claude Code puts a pasted block between <c>&lt;pasted_content id="…"&gt;</c> and
    /// <c>&lt;/pasted_content id="…"&gt;</c>, and escapes the same tag typed by hand (<c>&lt;\pasted_content</c>).
    /// </summary>
    internal static string Unpasted(string text)
    {
        if (!text.Contains("pasted_content", StringComparison.Ordinal)) return text;
        var unwrapped = PastedClose.Replace(PastedOpen.Replace(text, ""), "");
        return PastedEscaped.Replace(unwrapped, "<$1$2pasted_content");
    }

    /// <summary>
    /// <c>{"type":"queue-operation","operation":"enqueue","content":"&lt;task-notification&gt;…"}</c> (or, in older
    /// transcripts, <c>{"type":"attachment","attachment":{"type":"queued_command","prompt":…}}</c>): the notice of a
    /// background task, queued while Claude works. It may never come as a user line (absorbed into the turn), so the
    /// task's call ends here; the note is left to the user line, when there is one.
    /// </summary>
    private List<ConversationEvent> QueuedNotification(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var content = Str(root, "type") switch
            {
                "queue-operation" when Str(root, "operation") == "enqueue" => Str(root, "content"),
                "attachment" when Prop(root, "attachment") is { } attachment && Str(attachment, "type") == "queued_command" => Str(attachment, "prompt"),
                _ => null,
            };
            return content is not null && content.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal)
                ? TaskNotifications("", Timestamp(root), content.Trim(), notes: false)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// <c>{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"…"}}</c>: a message the
    /// user typed while Claude worked, given to Claude within the running turn (« absorbed mid-turn »), where the terminal
    /// shows it. It is a message of the user, in the turn that absorbed it: it starts no turn. A message queued until the
    /// turn ends comes as a user line instead, never as this attachment.
    /// </summary>
    private List<ConversationEvent> QueuedMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "attachment" || (!sidechain && True(root, "isSidechain"))
                || Prop(root, "attachment") is not { ValueKind: JsonValueKind.Object } attachment || Str(attachment, "type") != "queued_command"
                || (Str(attachment, "commandMode") ?? "prompt") != "prompt" || Str(root, "uuid") is not { } id)
                return [];
            return UserText(id, Timestamp(root), Str(attachment, "prompt"));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// What Claude Code tells the session about a background task (a command, a monitor, a sub-agent), sent as a user turn:
    /// <c>&lt;task-notification&gt;</c> blocks with the task's ids (<c>tool-use-id</c>, or only <c>task-id</c> for a monitor),
    /// a <c>summary</c>, an <c>event</c> and a <c>status</c>. The call that launched the task ends with it (done, or failed),
    /// and a note says so. A monitor sends each of its events this way: they update its call, no note, until
    /// « [Monitor expired … ».
    /// </summary>
    private List<ConversationEvent> TaskNotifications(string id, DateTime? at, string text, bool notes = true)
    {
        var events = new List<ConversationEvent>();
        var noteCount = 0;
        foreach (var block in text.Split("</task-notification>", StringSplitOptions.RemoveEmptyEntries))
        {
            if (Tag(block + "</task-notification>", "task-notification") is not { } body) continue;
            var reported = Tag(body, "status")?.ToLowerInvariant();
            var status = reported switch
            {
                "completed" => ToolStatuses.Done,
                "failed" or "killed" or "error" or "timeout" or "stopped" => ToolStatuses.Failed,
                _ => null,
            };
            var summary = Tag(body, "summary");
            var happened = Tag(body, "event");
            var callId = Tag(body, "tool-use-id")
                         ?? (Tag(body, "task-id") is { } taskId && _backgroundTasks.TryGetValue(taskId, out var launched) ? launched : null);
            var call = callId is not null && _calls.TryGetValue(callId, out var known) && known.Background == true ? known : null;
            // A task that ended stays so: a monitor's event can come after the notice that it expired
            if (call is not null && call.Status != ToolStatuses.Running) continue;
            var ended = reported is not null || call?.Tool != "Monitor"
                        || (happened?.StartsWith("[Monitor", StringComparison.Ordinal) == true
                            && (happened.Contains("expired", StringComparison.Ordinal) || happened.Contains("ended", StringComparison.Ordinal)
                                || happened.Contains("stopped", StringComparison.Ordinal)));
            if (call is not null)
            {
                var updated = call with
                {
                    Status = ended ? status ?? ToolStatuses.Done : ToolStatuses.Running,
                    Excerpt = Head(happened ?? summary ?? call.Excerpt, MaxExcerptLength),
                };
                _calls[call.Id] = updated;
                events.Add(updated);
                if (!ended) continue;
            }
            var said = summary ?? happened;
            if (!notes || string.IsNullOrWhiteSpace(said)) continue;
            events.Add(new ConversationEvent
            {
                Id = noteCount++ == 0 ? id : $"{id}:{noteCount - 1}", Kind = ConversationKinds.Note, At = at, Label = "task",
                Text = Head(said, MaxTitleLength), Status = status,
            });
        }
        return events;
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
        // Launched in the background (asked so, or moved there): it goes on until its notice (TaskNotifications)
        var taskId = output is null ? null : BackgroundTask.Match(output) is { Success: true } m ? m.Groups["id"].Value : null;
        if (!failed && (call.Background == true || taskId is not null))
        {
            if (taskId is not null) _backgroundTasks[taskId] = callId;
            var launched = call with { Status = ToolStatuses.Running, Background = true, Excerpt = Head(output ?? call.Excerpt, MaxExcerptLength) };
            _calls[callId] = launched;
            return launched;
        }
        var updated = call with
        {
            Status = failed ? ToolStatuses.Failed : ToolStatuses.Done,
            Background = call.Background == true ? false : null,
            // An edit keeps its diff; any other tool shows the start of what it returned, a failure always its error
            Excerpt = Head(failed || call.Category != ToolCategories.Edit || call.Excerpt is null ? output ?? call.Excerpt : call.Excerpt, MaxExcerptLength),
            Images = failed ? null : ResultImages(callId, call, Prop(part, "content"), output),
        };
        _calls[callId] = updated;
        return updated;
    }

    /// <summary>A link to an image file in a tool's result: <c>[Screenshot of viewport](.playwright-mcp/page.png)</c>.</summary>
    private static readonly Regex ImageLink = new(@"\]\(([^)\s]+\.(?:png|jpe?g|webp))\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The images a call's result showed the CLI (doc 70), kept in <see cref="Images"/>: the image parts of the result
    /// (claude's <c>Read</c> of an image, a tool returning one), else the file a screenshot tool says it saved (Playwright
    /// MCP's <c>browser_take_screenshot</c> with a file name only links it). Null when there is none.
    /// </summary>
    private IReadOnlyList<string>? ResultImages(string callId, ConversationEvent call, JsonElement? content, string? output)
    {
        List<string>? names = null;
        void Add(string? data, string? mediaType, string? filePath)
        {
            if (names?.Count >= MaxImages || ToolImages.NameFor(callId, (names?.Count ?? 0) + 1, mediaType, filePath) is not { } name) return;
            Images.Add(new ToolImage(name, callId, call.At, data, mediaType, filePath));
            (names ??= []).Add(name);
        }
        _readPaths.Remove(callId, out var readPath);
        if (content is { ValueKind: JsonValueKind.Array } parts)
            foreach (var part in parts.EnumerateArray())
                if (Str(part, "type") == "image" && Prop(part, "source") is { ValueKind: JsonValueKind.Object } source
                    && Str(source, "type") == "base64" && Str(source, "data") is { Length: > 0 } data)
                    Add(data, Str(source, "media_type"), readPath);
        if (names is null && output is not null && call.Tool is { } tool && tool.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            foreach (Match link in ImageLink.Matches(output))
            {
                try { Add(null, null, Path.GetFullPath(Path.Combine(cwd, link.Groups[1].Value))); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        return names;
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
                    {
                        events.Add(call);
                        if (Stopped(call.Tool, Prop(part, "input") ?? default) is { } stopped) events.Add(stopped);
                    }
                    break;
                // What Claude says between two steps, which the terminal shows as its text
                case "thinking" when Str(part, "thinking") is { } said && !string.IsNullOrWhiteSpace(said) && IsNarration(Str(part, "signature")):
                    events.Add(new ConversationEvent { Id = partId, Kind = ConversationKinds.Assistant, At = at, Text = Head(said.Trim(), MaxTextLength) });
                    break;
                case { } type when SkippedParts.Contains(type):
                    break;
                case { } other:
                    events.Add(Unrecognized(partId, at, $"assistant:{other}"));
                    break;
            }
        }
        // Each line of a response repeats its usage so far: counted once per response (message id)
        var tokens = Prop(message, "usage") is { } usage && Prop(usage, "output_tokens") is { ValueKind: JsonValueKind.Number } output
                     && output.TryGetInt64(out var count) ? count : 0;
        if (_turn.AddResponse(Str(message, "id"), tokens, at) is { } turn)
            events.Add(turn);
        return events;
    }

    /// <summary>
    /// A <c>thinking</c> part the API tagged as narration: the few words the model says to the user between two steps
    /// (« La CI est verte, je pousse… »), which Claude Code shows as text, not as thinking. The tag is in the part's
    /// signature, a base64 protobuf: field 2, then its field 1, then its field 8 is the string <c>narration</c> (as
    /// Claude Code 2.1.318 reads it). Any other thinking stays out of the thread.
    /// </summary>
    public static bool IsNarration(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(signature.Length % 4 == 0 ? signature : signature.PadRight(signature.Length + 4 - signature.Length % 4, '='));
        }
        catch (FormatException)
        {
            return false;
        }
        return Field(bytes, 2) is { } block && Field(block, 1) is { } info && Field(info, 8) is { } kind
               && kind.Span.SequenceEqual("narration"u8);
    }

    /// <summary>The first length-delimited field <paramref name="number"/> of a protobuf message, null when absent or malformed.</summary>
    private static ReadOnlyMemory<byte>? Field(ReadOnlyMemory<byte> message, int number)
    {
        var span = message.Span;
        var i = 0;
        while (i < span.Length)
        {
            if (!Varint(span, ref i, out var tag)) return null;
            switch (tag & 7)
            {
                case 0:
                    if (!Varint(span, ref i, out _)) return null;
                    break;
                case 1:
                    i += 8;
                    break;
                case 5:
                    i += 4;
                    break;
                case 2:
                    if (!Varint(span, ref i, out var length) || length > (ulong)(span.Length - i)) return null;
                    if (tag >> 3 == (ulong)number) return message.Slice(i, (int)length);
                    i += (int)length;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool Varint(ReadOnlySpan<byte> span, ref int i, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && i < span.Length; shift += 7)
        {
            var b = span[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    private ConversationEvent? ToolCall(JsonElement part, DateTime? at)
    {
        if (Str(part, "id") is not { } callId || Str(part, "name") is not { } name)
            return null;
        var input = Prop(part, "input") ?? default;
        var (category, title, excerpt) = Describe(name, input);
        var background = name == "Monitor" || True(input, "run_in_background");
        var call = new ConversationEvent
        {
            Id = callId, Kind = ConversationKinds.Tool, At = at, Tool = name, Category = category,
            Title = Head(title, MaxTitleLength), Excerpt = Head(excerpt, MaxExcerptLength),
            // A server tool (web search) runs on Anthropic's side: its result comes in the same message
            Status = Str(part, "type") == "server_tool_use" ? ToolStatuses.Done : ToolStatuses.Running,
            Background = background ? true : null,
        };
        // A call written again (same id) keeps the result it already has
        if (_calls.TryGetValue(callId, out var known) && (known.Status != ToolStatuses.Running || known.Background == true))
            call = call with { Status = known.Status, Excerpt = known.Excerpt, Background = known.Background, Images = known.Images };
        else if (name == "Read" && Str(input, "file_path") is { Length: > 0 } readPath)
            _readPaths[callId] = readPath;
        _calls[callId] = call;
        return call;
    }

    /// <summary>Claude stops a background task itself (<c>TaskStop</c>, <c>KillShell</c> before it): the task's call ends.</summary>
    private ConversationEvent? Stopped(string? tool, JsonElement input)
    {
        if (tool is not ("TaskStop" or "KillShell" or "KillBash")
            || (Str(input, "task_id") ?? Str(input, "shell_id")) is not { } taskId
            || !_backgroundTasks.TryGetValue(taskId, out var callId)
            || !_calls.TryGetValue(callId, out var call) || call.Status != ToolStatuses.Running)
            return null;
        var stopped = call with { Status = ToolStatuses.Done };
        _calls[callId] = stopped;
        return stopped;
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
            case "Monitor":
                return (ToolCategories.Run, FirstLine(Str(input, "description") ?? Str(input, "command")), null);
            case "Task":
            case "Agent":
                return (ToolCategories.Task, FirstLine(Str(input, "description") ?? Str(input, "prompt")), null);
            case "TodoWrite":
                return (ToolCategories.Task, null, null);
            default:
                // An MCP tool: what it works on (Playwright: the page's URL, the screenshot's file)
                return (ToolCategories.Other, FirstLine(Str(input, "description") ?? Str(input, "command") ?? Path()
                    ?? Str(input, "url") ?? Relative(Str(input, "filename"), cwd)), null);
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
