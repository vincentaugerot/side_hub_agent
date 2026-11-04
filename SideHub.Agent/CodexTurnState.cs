using System.Text.Json;

namespace SideHub.Agent;

/// <summary>
/// What a codex session is doing, from its rollout: <c>task_started</c> opens a turn (working), <c>task_complete</c> or
/// <c>turn_aborted</c> (interrupted) ends it (idle). Codex's <c>notify</c> only tells the end of a turn, and its terminal
/// redraws itself after it, so its output cannot tell a new turn: the rollout can.
/// </summary>
public static class CodexTurnState
{
    /// <summary>A rollout line is read whole to find its type: a turn's last line holds its last answer.</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    /// <summary>The state the lines leave the session in, or null when none of them starts or ends a turn.</summary>
    public static string? Of(IEnumerable<string> lines)
    {
        string? state = null;
        foreach (var line in lines)
            state = OfLine(line) ?? state;
        return state;
    }

    /// <summary><c>{"type":"event_msg","payload":{"type":"task_started"}}</c> and the like; null for any other line.</summary>
    public static string? OfLine(string line)
    {
        if (!line.Contains("\"event_msg\"", StringComparison.Ordinal)
            || !(line.Contains("\"task_started\"", StringComparison.Ordinal)
                 || line.Contains("\"task_complete\"", StringComparison.Ordinal)
                 || line.Contains("\"turn_aborted\"", StringComparison.Ordinal)))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String)
                return null;
            return kind.GetString() switch
            {
                "task_started" => CliStates.Working,
                "task_complete" or "turn_aborted" => CliStates.Idle,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
