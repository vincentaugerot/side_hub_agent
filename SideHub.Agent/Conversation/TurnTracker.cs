namespace SideHub.Agent.Conversation;

/// <summary>
/// The turn in progress in a conversation (doc 69), from a message of the user (or the notice of a background task, which
/// starts a turn too) to the CLI's last response: when it started, when the CLI last wrote, and the tokens the model
/// produced, as the « 1m 52s · ↓ 6.0k tokens » of Claude Code. Sent as a <see cref="ConversationKinds.Turn"/> event whose
/// id is tied to the event that started it (<c>turn:&lt;id&gt;</c>), sent again with the same id at each change.
/// </summary>
internal sealed class TurnTracker
{
    private string? _id;
    private DateTime? _startedAt;
    private DateTime? _endedAt;
    private long _tokens;
    /// <summary>Tokens counted per model response: Claude writes a response in several lines, each with its usage.</summary>
    private readonly Dictionary<string, long> _byResponse = new(StringComparer.Ordinal);

    /// <summary>A turn starts with the event <paramref name="eventId"/>.</summary>
    public ConversationEvent Start(string eventId, DateTime? at)
    {
        _id = $"turn:{eventId}";
        _startedAt = at;
        _endedAt = at;
        _tokens = 0;
        _byResponse.Clear();
        return Event();
    }

    /// <summary>
    /// A model response of the turn with its output tokens so far (the highest count seen for it is kept); null when no
    /// turn started or nothing changed.
    /// </summary>
    public ConversationEvent? AddResponse(string? responseId, long outputTokens, DateTime? at)
    {
        if (_id is null) return null;
        var added = Math.Max(0, outputTokens);
        if (responseId is not null)
        {
            var counted = _byResponse.GetValueOrDefault(responseId);
            added = Math.Max(0, outputTokens - counted);
            _byResponse[responseId] = Math.Max(counted, outputTokens);
        }
        _tokens += added;
        return Later(at) | added > 0 ? Event() : null;
    }

    /// <summary>The turn's tokens counted elsewhere (Codex: its session's total less the total when the turn started).</summary>
    public ConversationEvent? SetTokens(long tokens, DateTime? at)
    {
        if (_id is null) return null;
        var changed = tokens >= 0 && tokens != _tokens;
        if (changed) _tokens = tokens;
        return Later(at) | changed ? Event() : null;
    }

    /// <summary>The CLI wrote something of the turn at <paramref name="at"/>.</summary>
    public ConversationEvent? Touch(DateTime? at) => _id is not null && Later(at) ? Event() : null;

    private bool Later(DateTime? at)
    {
        if (at is null || (_endedAt is not null && at <= _endedAt)) return false;
        _endedAt = at;
        return true;
    }

    private ConversationEvent Event() => new()
    {
        Id = _id!, Kind = ConversationKinds.Turn, At = _startedAt, EndedAt = _endedAt, Tokens = _tokens,
    };
}
