using System.Text.Json;

namespace SideHub.Agent.Conversation;

/// <summary>
/// What the sub-agents of a claude session are doing (doc 69), for the call that launched each one: Claude Code writes a
/// sub-agent's transcript beside the session's, in <c>&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>, with a
/// <c>agent-&lt;id&gt;.meta.json</c> whose <c>toolUseId</c> is that call. Each running call's file is followed from its
/// start: the tool calls the sub-agent made, its last one, and the tokens it produced. Not thread-safe, as the feed.
/// </summary>
public sealed class ClaudeSubagents(string transcriptPath, string cwd)
{
    /// <summary>What a sub-agent did so far.</summary>
    public sealed record Progress(int Steps, string? Category, string? Title, long Tokens);

    /// <summary>Tools of the sub-agent that are not steps of its work: it hands its report back with one.</summary>
    private static readonly HashSet<string> NotSteps = new(StringComparer.Ordinal) { "SubagentHandback" };

    private readonly string _directory = Path.Combine(
        Path.GetDirectoryName(transcriptPath) ?? "", Path.GetFileNameWithoutExtension(transcriptPath), "subagents");

    /// <summary>Meta files read, with the call they name (null: none).</summary>
    private readonly Dictionary<string, string?> _metas = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Follower> _byCall = new(StringComparer.Ordinal);

    private sealed class Follower(string path, string cwd)
    {
        public readonly JsonlTail Tail = new(path, ConversationFeed.MaxLineBytes);
        public readonly ClaudeConversationParser Parser = new(cwd, sidechain: true);
        public readonly HashSet<string> Steps = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> TurnTokens = new(StringComparer.Ordinal);
        public ConversationEvent? Last;
        public Progress? Sent;
    }

    /// <summary>The progress of the sub-agents of <paramref name="runningCalls"/> (calls still running) that changed.</summary>
    public IReadOnlyDictionary<string, Progress> Poll(IReadOnlyCollection<string> runningCalls)
    {
        var changed = new Dictionary<string, Progress>(StringComparer.Ordinal);
        if (runningCalls.Count == 0) return changed;
        if (runningCalls.Any(id => !_byCall.ContainsKey(id))) ReadNewMetas();
        foreach (var callId in runningCalls)
        {
            if (!_byCall.TryGetValue(callId, out var follower)) continue;
            try
            {
                foreach (var line in follower.Tail.ReadNewLines())
                    foreach (var e in follower.Parser.Parse(line))
                    {
                        if (e.Kind == ConversationKinds.Turn) follower.TurnTokens[e.Id] = e.Tokens ?? 0;
                        else if (e.Kind == ConversationKinds.Tool && e.Tool is { } tool && !NotSteps.Contains(tool) && follower.Steps.Add(e.Id))
                            follower.Last = e;
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Its transcript is not written yet, or was removed: read again at the next poll
                continue;
            }
            var progress = new Progress(follower.Steps.Count, follower.Last?.Category, follower.Last?.Title, follower.TurnTokens.Values.Sum());
            if (progress == follower.Sent) continue;
            follower.Sent = progress;
            changed[callId] = progress;
        }
        return changed;
    }

    private void ReadNewMetas()
    {
        string[] metas;
        try
        {
            if (!Directory.Exists(_directory)) return;
            metas = Directory.GetFiles(_directory, "agent-*.meta.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var meta in metas)
        {
            if (_metas.ContainsKey(meta)) continue;
            var callId = ToolUseId(meta);
            // A meta written before its call id is known is read again later
            if (callId is null) continue;
            _metas[meta] = callId;
            var transcript = meta[..^".meta.json".Length] + ".jsonl";
            _byCall.TryAdd(callId, new Follower(transcript, cwd));
        }
    }

    private static string? ToolUseId(string meta)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(meta));
            return ConversationText.Str(doc.RootElement, "toolUseId");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
