using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>Writes a CLI's JSON config the launcher changes (trusted folders, hooks), so the CLI never reads half a file.</summary>
public static class ConfigFiles
{
    // Quotes and apostrophes as is: the user may read and edit these files (agy's hooks hold shell commands).
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="root"/>, after <paramref name="header"/> (comment lines the
    /// CLI heads its file with) when given. See <see cref="ReplaceText"/>.
    /// </summary>
    public static void Replace(string path, JsonObject root, string? header = null) =>
        ReplaceText(path, (header ?? "") + root.ToJsonString(Options) + (header is null ? "" : "\n"));

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/>: written to a private temporary file next to it,
    /// then moved over it. Its folder is created when missing.
    /// </summary>
    public static void ReplaceText(string path, string content)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        var temporary = $"{path}.sidehub-{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
