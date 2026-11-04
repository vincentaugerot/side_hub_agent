using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SideHub.Agent.Conversation;

/// <summary>Small helpers shared by the conversation parsers: JSON reads that never throw, lengths, paths.</summary>
internal static class ConversationText
{
    /// <summary>Longest text of a message sent, in characters (as <see cref="CliMessageTracker.MaxLength"/>).</summary>
    public const int MaxTextLength = 64_000;

    /// <summary>Longest excerpt of a tool's result.</summary>
    public const int MaxExcerptLength = 2_000;

    /// <summary>Longest title of a tool call (a path, a command line).</summary>
    public const int MaxTitleLength = 200;

    public static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool True(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static JsonElement? Prop(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : null;

    public static DateTime? Timestamp(JsonElement obj) =>
        Str(obj, "timestamp") is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>Cut to <paramref name="max"/> characters, with a mark where it was cut.</summary>
    public static string? Cap(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// The start of a text read from a transcript, before its secrets are masked: four times what is sent, so that a secret
    /// cut here lies past the final cut (<see cref="Cap"/>, once masked) and never shows in part.
    /// </summary>
    public static string? Head(string? text, int max) =>
        text is null || text.Length <= max * 4 ? text : text[..(max * 4)];

    /// <summary>The first line of a command, as a title.</summary>
    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        var line = newline < 0 ? trimmed : trimmed[..newline].TrimEnd() + " …";
        return Head(line, MaxTitleLength);
    }

    /// <summary>A path under the session's directory, shown relative to it.</summary>
    public static string? Relative(string? path, string cwd)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith("file://", StringComparison.Ordinal)) path = path["file://".Length..];
        var root = cwd.TrimEnd('/', '\\');
        if (root.Length > 0 && path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.Ordinal)
            && path[root.Length] is '/' or '\\')
            return path[(root.Length + 1)..];
        return path;
    }

    /// <summary>
    /// A tool result as text: a string, or the text parts of an array (<c>[{ "type": "text", "text": … }]</c>); other
    /// parts (an image) are named in brackets.
    /// </summary>
    public static string? ResultText(JsonElement? content)
    {
        if (content is not { } value) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder();
        foreach (var part in value.EnumerateArray())
        {
            var type = Str(part, "type");
            var piece = type is "text" or "input_text" or "output_text" ? Str(part, "text") : type is null ? null : $"[{type}]";
            if (string.IsNullOrEmpty(piece)) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(piece);
            if (text.Length > MaxExcerptLength) break;
        }
        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>An edit as a few diff lines: the old text with <c>-</c>, the new one with <c>+</c>.</summary>
    public static string? Diff(string? oldText, string? newText)
    {
        var diff = new StringBuilder();
        void Lines(string? text, char mark)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var line in text.TrimEnd('\n', '\r').Split('\n'))
            {
                if (diff.Length > MaxExcerptLength) return;
                diff.Append(mark).Append(' ').Append(line.TrimEnd('\r')).Append('\n');
            }
        }
        Lines(oldText, '-');
        Lines(newText, '+');
        return diff.Length > 0 ? diff.ToString().TrimEnd('\n') : null;
    }
}
