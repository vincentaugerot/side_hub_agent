using System.IO.Compression;
using System.Text.Json;

namespace SideHub.Cli.Okf;

/// <summary>
/// `okf export` and `okf import`: a folder of the workspace's Mémoire to an OKF bundle on disk (or a zip), and a bundle
/// of the repository into the Mémoire. Importing the same bundle again updates the pages whose text changed.
/// </summary>
public static class OkfBundleCommands
{
    public static async Task<int> ExportAsync(SideHubApiClient client, string[] args, bool json)
    {
        var path = Option(args, "--path");
        var folderId = path is null ? Positional(args, "--out", "--path") : null;
        var name = "memoire";
        if (path is not null && path.Trim('/').Length > 0)
        {
            var tree = await client.GetDriveTreeAsync();
            var folder = DriveTree.FindByPath(tree.TryGetProperty("items", out var items) ? items : tree, path.TrimEnd('/') + "/");
            if (folder is null)
            {
                Console.Error.WriteLine($"No folder at {path} (path from the workspace root, folder titles in lowercase with dashes)");
                return 1;
            }
            folderId = DriveTree.Str(folder.Value, "id");
            name = DriveTree.Slug(DriveTree.Str(folder.Value, "title"));
        }
        else if (folderId is not null)
        {
            var item = await client.GetDriveItemAsync(folderId);
            name = DriveTree.Slug(DriveTree.Str(item, "title"));
        }

        var zip = await client.ExportOkfBundleAsync(folderId);
        var output = Option(args, "--out") ?? (name.Length == 0 ? "memoire" : name);
        int written;
        if (output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllBytesAsync(output, zip);
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            written = archive.Entries.Count;
        }
        else
        {
            written = Extract(zip, output);
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { output = Path.GetFullPath(output), files = written }));
        else
            Console.WriteLine(output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? $"OKF bundle written to {output} ({written} file(s))."
                : $"OKF bundle written to {output} ({written} file(s)). Check it with `sidehub-cli okf lint {output}`.");
        return 0;
    }

    public static async Task<int> ImportAsync(SideHubApiClient client, string[] args, bool json)
    {
        var source = Positional(args, "--into", "--into-path");
        if (source is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli okf import <folder|bundle.zip> [--into <folderId> | --into-path </folder/>]");
            return 1;
        }

        var parentId = Option(args, "--into");
        if (Option(args, "--into-path") is { } intoPath && intoPath.Trim('/').Length > 0)
        {
            var tree = await client.GetDriveTreeAsync();
            var folder = DriveTree.FindByPath(tree.TryGetProperty("items", out var items) ? items : tree, intoPath.TrimEnd('/') + "/");
            if (folder is null)
            {
                Console.Error.WriteLine($"No folder at {intoPath}: create it first (`sidehub-cli drive create --title \"<title>\" --type folder`)");
                return 1;
            }
            parentId = DriveTree.Str(folder.Value, "id");
        }

        byte[] zip;
        if (File.Exists(source)) zip = await File.ReadAllBytesAsync(source);
        else if (Directory.Exists(source)) zip = Pack(source);
        else
        {
            Console.Error.WriteLine($"Not found: {source}");
            return 1;
        }

        var result = await client.ImportOkfBundleAsync(zip, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))) + (File.Exists(source) ? "" : ".zip"), parentId);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        int N(string p) => result.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        Console.WriteLine($"Imported: {N("pagesCreated")} page(s) created, {N("pagesUpdated")} updated, {N("pagesUnchanged")} unchanged, " +
                          $"{N("foldersCreated")} folder(s) and {N("filesCreated")} file(s) added.");
        if (result.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            foreach (var warning in warnings.EnumerateArray())
                Console.Error.WriteLine($"warning: {warning.GetString()}");
        return 0;
    }

    /// <summary>The bundle folder as a zip, its content at the root; hidden files and folders (`.git`) left out.</summary>
    public static byte[] Pack(string folder)
    {
        var root = Path.GetFullPath(folder);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Split('/').Any(s => s.StartsWith('.'))) continue;
                zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Unzips into <paramref name="folder"/>, overwriting files of the same path; an entry leaving it is refused.</summary>
    public static int Extract(byte[] zip, string folder)
    {
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe path in the bundle: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }

    private static string? Option(string[] args, string flag)
    {
        var at = Array.IndexOf(args, flag);
        return at >= 0 && at < args.Length - 1 ? args[at + 1] : null;
    }

    /// <summary>First argument that is neither a flag nor the value of one of <paramref name="valued"/>.</summary>
    private static string? Positional(string[] args, params string[] valued)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (valued.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) return args[i];
        }
        return null;
    }
}
