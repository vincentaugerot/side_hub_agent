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

    /// <param name="mask">Masks the PTY's secrets in a text (identity when it has none).</param>
    public ConversationFeed(string provider, string cliSessionId, string path, IConversationParser parser, Func<string?, string?> mask)
    {
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
        {
            foreach (var parsed in _parser.Parse(line))
            {
                var masked = Mask(parsed);
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
        }
        if (_events.Count > MaxEvents)
            _events.RemoveRange(0, _events.Count - MaxEvents);
        return changed;
    }

    /// <summary>Types met in the transcript that the parser does not know, each once.</summary>
    public IReadOnlyList<string> TakeUnrecognized() => _parser.TakeUnrecognized();

    /// <summary>Secrets masked first, then cut: a cut never leaves the start of a secret.</summary>
    private ConversationEvent Mask(ConversationEvent e) => e with
    {
        Text = ConversationText.Cap(_mask(e.Text), ConversationText.MaxTextLength),
        Title = ConversationText.Cap(_mask(e.Title), ConversationText.MaxTitleLength),
        Excerpt = ConversationText.Cap(_mask(e.Excerpt), ConversationText.MaxExcerptLength),
    };

    /// <summary>The parser of a CLI's transcript, null for a CLI without Conversation mode.</summary>
    public static IConversationParser? ParserFor(string provider, string cwd) => provider switch
    {
        "claude" => new ClaudeConversationParser(cwd),
        "codex" => new CodexConversationParser(cwd),
        _ => null,
    };
}
