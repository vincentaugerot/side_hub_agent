namespace SideHub.Agent.Conversation;

/// <summary>
/// The conversation of one CLI session, followed in its transcript while someone looks at it in Conversation mode (doc
/// 69): the first <see cref="Poll"/> reads the whole file, the next ones only the lines written since. Keeps the last
/// <see cref="MaxEvents"/> events for the snapshot sent when a viewer opens the mode. Not thread-safe: one watcher polls it.
/// </summary>
public sealed class ConversationFeed
{
    /// <summary>Events kept for a snapshot: the terminal has the rest.</summary>
    public const int MaxEvents = 200;

    /// <summary>Longest snapshot, in characters of text: the oldest events are left out past it.</summary>
    public const int MaxSnapshotChars = 1_000_000;

    /// <summary>Longest transcript line read (a pasted image is far longer and carries nothing to show).</summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private readonly JsonlTail _tail;
    private readonly IConversationParser _parser;
    private readonly Func<string?, string?> _mask;
    private readonly List<ConversationEvent> _events = [];
    private readonly ClaudeSubagents? _subagents;
    /// <summary>The last progress of each sub-agent's call, kept on the call when the parser writes it again.</summary>
    private readonly Dictionary<string, ClaudeSubagents.Progress> _progress = new(StringComparer.Ordinal);

    /// <summary>Tools of claude that run a sub-agent.</summary>
    private static readonly HashSet<string> SubagentTools = new(StringComparer.Ordinal) { "Agent", "Task" };

    /// <param name="mask">Masks the PTY's secrets in a text (identity when it has none).</param>
    /// <param name="subagents">Follows the sub-agents of the session (claude), null for none.</param>
    public ConversationFeed(string provider, string cliSessionId, string path, IConversationParser parser, Func<string?, string?> mask,
        ClaudeSubagents? subagents = null)
    {
        _subagents = subagents;
        Provider = provider;
        CliSessionId = cliSessionId;
        Path = path;
        _tail = new JsonlTail(path, MaxLineBytes);
        _parser = parser;
        _mask = mask;
    }

    public string Provider { get; }
    public string CliSessionId { get; }
    public string Path { get; }

    /// <summary>The images of the tool calls read so far, served to the viewers who ask for them (doc 70).</summary>
    public ToolImages Images => _parser.Images;

    /// <summary>The kept events, oldest first, within <see cref="MaxSnapshotChars"/>.</summary>
    public IReadOnlyList<ConversationEvent> Snapshot()
    {
        var size = 0;
        var start = _events.Count;
        while (start > 0 && size + _events[start - 1].Size <= MaxSnapshotChars)
            size += _events[--start].Size;
        return _events.Skip(start).ToList();
    }

    /// <summary>
    /// Reads the lines written since the last call: the events added or changed, in the order of their first change
    /// (a tool call changed twice is sent once, as it is now). Secrets masked.
    /// </summary>
    public IReadOnlyList<ConversationEvent> Poll()
    {
        var changed = new List<ConversationEvent>();
        foreach (var line in _tail.ReadNewLines())
            foreach (var parsed in _parser.Parse(line))
                Keep(parsed, changed);
        if (_subagents is not null)
        {
            var running = _events
                .Where(e => e.Kind == ConversationKinds.Tool && e.Status == ToolStatuses.Running && e.Tool is { } tool && SubagentTools.Contains(tool))
                .Select(e => e.Id).ToList();
            foreach (var (callId, progress) in _subagents.Poll(running))
            {
                _progress[callId] = progress;
                if (_events.Find(e => e.Id == callId) is { } call) Keep(call, changed);
            }
        }
        if (_events.Count > MaxEvents)
            _events.RemoveRange(0, _events.Count - MaxEvents);
        return changed;
    }

    /// <summary>An event added or changed: kept (with its sub-agent's progress), and listed once in <paramref name="changed"/>.</summary>
    private void Keep(ConversationEvent parsed, List<ConversationEvent> changed)
    {
        var masked = Mask(_progress.TryGetValue(parsed.Id, out var p)
            ? parsed with { Steps = p.Steps, StepCategory = p.Category, StepTitle = p.Title, Tokens = p.Tokens }
            : parsed);
        var kept = _events.FindIndex(e => e.Id == masked.Id);
        if (kept >= 0)
            _events[kept] = masked;
        else
            _events.Add(masked);
        var sent = changed.FindIndex(e => e.Id == masked.Id);
        if (sent >= 0)
            changed[sent] = masked;
        else
            changed.Add(masked);
    }

    /// <summary>Types met in the transcript that the parser does not know, each once.</summary>
    public IReadOnlyList<string> TakeUnrecognized() => _parser.TakeUnrecognized();

    /// <summary>Secrets masked first, then cut: a cut never leaves the start of a secret.</summary>
    private ConversationEvent Mask(ConversationEvent e) => e with
    {
        Text = ConversationText.Cap(_mask(e.Text), ConversationText.MaxTextLength),
        Title = ConversationText.Cap(_mask(e.Title), ConversationText.MaxTitleLength),
        From = ConversationText.Cap(_mask(e.From), ConversationText.MaxTitleLength),
        Excerpt = ConversationText.Cap(_mask(e.Excerpt), ConversationText.MaxExcerptLength),
        StepTitle = ConversationText.Cap(_mask(e.StepTitle), ConversationText.MaxTitleLength),
    };

    /// <summary>
    /// The parser of a CLI's transcript, null for a CLI without Conversation mode. <paramref name="imageDirectories"/>: the
    /// folders of the images pasted from SideHub, as written in the PTY.
    /// </summary>
    /// <summary>Whether the CLI has a Conversation mode (<see cref="ParserFor"/> knows its transcript).</summary>
    public static bool Supports(string provider) => provider is "claude" or "codex";

    public static IConversationParser? ParserFor(string provider, string cwd, IReadOnlyCollection<string>? imageDirectories = null) => provider switch
    {
        "claude" => new ClaudeConversationParser(cwd, imageDirectories),
        "codex" => new CodexConversationParser(cwd, imageDirectories),
        _ => null,
    };

    /// <summary>What follows the sub-agents of a CLI's session: claude only (codex's sub-agents are not read).</summary>
    public static ClaudeSubagents? SubagentsFor(string provider, string path, string cwd) =>
        provider == "claude" ? new ClaudeSubagents(path, cwd) : null;
}
