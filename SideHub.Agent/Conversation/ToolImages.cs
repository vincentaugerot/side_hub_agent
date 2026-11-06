using System.Text.RegularExpressions;

namespace SideHub.Agent.Conversation;

/// <summary>
/// An image a tool call showed the CLI (doc 70): a Playwright screenshot, an image file it read. Either inline in the
/// transcript (<see cref="Data"/>, base64: claude's <c>Read</c> of an image, a tool that returns one) or a file the call
/// points to (<see cref="FilePath"/>: a screenshot saved to disk, codex's <c>ImageView</c>). Both when the call read a
/// file whose content the transcript also holds: the data is what the CLI saw, the file may have changed since.
/// </summary>
/// <param name="Name">Plain image file name (<see cref="ConversationText.IsImageName"/>), unique in the session.</param>
/// <param name="CallId">The tool call that showed it.</param>
public sealed record ToolImage(string Name, string CallId, DateTime? At, string? Data, string? MediaType, string? FilePath);

/// <summary>
/// The images of a conversation's tool calls, by name, in the order they came. The last <see cref="MaxWithData"/> keep
/// their inline data; an older one keeps only its file, if it has one. Read by the viewers' image requests while the
/// feed is polled: thread-safe.
/// </summary>
public sealed class ToolImages
{
    public const int MaxKept = 200;
    public const int MaxWithData = 30;

    /// <summary>Largest image sent to a viewer or kept for a review (a screenshot of a long page stays below).</summary>
    public const long MaxBytes = 6 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly List<ToolImage> _images = [];

    public void Add(ToolImage image)
    {
        lock (_lock)
        {
            _images.RemoveAll(i => i.Name == image.Name);
            _images.Add(image);
            if (_images.Count > MaxKept)
                _images.RemoveRange(0, _images.Count - MaxKept);
            var withData = _images.Count - MaxWithData - 1;
            if (withData >= 0 && _images[withData].Data is not null)
                _images[withData] = _images[withData] with { Data = null };
        }
    }

    public ToolImage? Find(string name)
    {
        lock (_lock)
            return _images.Find(i => i.Name == name);
    }

    /// <summary>The images kept, oldest first.</summary>
    public IReadOnlyList<ToolImage> All()
    {
        lock (_lock)
            return _images.ToList();
    }

    private static readonly Regex UnsafeChars = new(@"[^A-Za-z0-9._-]", RegexOptions.CultureInvariant);

    /// <summary>
    /// The name of the <paramref name="index"/>th image of a call, as a plain file name: <c>toolu_01ab-1.png</c>. Null when
    /// the type is not one a viewer can show (png, jpeg, webp).
    /// </summary>
    public static string? NameFor(string callId, int index, string? mediaType, string? filePath)
    {
        var extension = ExtensionOf(mediaType) ?? ExtensionOf(Path.GetExtension(filePath ?? ""));
        if (extension is null) return null;
        var id = UnsafeChars.Replace(callId, "-");
        if (id.Length > 100) id = id[..100];
        var name = $"{(id.Length > 0 && char.IsAsciiLetterOrDigit(id[0]) ? "" : "t")}{id}-{index}{extension}";
        return ConversationText.IsImageName(name) ? name : null;
    }

    /// <summary>A media type (<c>image/png</c>) or a file extension (<c>.jpeg</c>) as the extension of an image name.</summary>
    private static string? ExtensionOf(string? type) => type?.ToLowerInvariant() switch
    {
        "image/png" or ".png" => ".png",
        "image/jpeg" or "image/jpg" or ".jpg" or ".jpeg" => ".jpg",
        "image/webp" or ".webp" => ".webp",
        _ => null,
    };

    /// <summary>The media type of an image name.</summary>
    public static string MediaTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>What <see cref="Load"/> gives: the image's bytes, or why not (<c>not-found</c>, <c>too-large</c>, <c>invalid</c>).</summary>
    public sealed record Loaded(byte[]? Bytes, string? Error);

    /// <summary>
    /// The bytes of an image: its inline data, else its file (an image of at most <see cref="MaxBytes"/>, read as it is
    /// now). Never throws for a file gone or unreadable.
    /// </summary>
    public static Loaded Load(ToolImage image)
    {
        if (image.Data is { } data)
        {
            // Base64 is 4 characters for 3 bytes
            if (data.Length / 4 * 3 > MaxBytes) return new Loaded(null, "too-large");
            try { return new Loaded(Convert.FromBase64String(data), null); }
            catch (FormatException) { return new Loaded(null, "invalid"); }
        }
        if (image.FilePath is not { } path || ExtensionOf(Path.GetExtension(path)) is null)
            return new Loaded(null, "not-found");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return new Loaded(null, "not-found");
            if (file.Length > MaxBytes) return new Loaded(null, "too-large");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return new Loaded(null, "too-large");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return new Loaded(bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Loaded(null, "not-found");
        }
    }
}
