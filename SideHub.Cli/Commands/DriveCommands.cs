using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Cli.Okf;

namespace SideHub.Cli.Commands;

public static class DriveCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var filter = new DriveTree.OkfFilter(GetOption(args, "--type"), GetOption(args, "--tag"), GetOption(args, "--status"), args.Contains("--stale"));
        var result = await client.GetDriveTreeAsync();

        if (filter.Any)
            return ListConcepts(result, filter, json);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            Console.WriteLine("No items in drive.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-8} {"TITLE",-40} {"UPDATED"}");
        Console.WriteLine(new string('-', 110));
        PrintTree(items, 0);
        return 0;
    }

    /// <summary>`drive list --type|--tag|--status|--stale`: the OKF pages matching, flat, newest first.</summary>
    private static int ListConcepts(JsonElement tree, DriveTree.OkfFilter filter, bool json)
    {
        var items = tree.TryGetProperty("items", out var i) ? i : tree;
        var now = DateTimeOffset.UtcNow;
        var pages = DriveTree.Concepts(items, filter, now)
            .OrderByDescending(p => DriveTree.Str(p, "updatedAt"), StringComparer.Ordinal)
            .ToList();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(pages));
            return 0;
        }
        if (pages.Count == 0)
        {
            Console.WriteLine("No OKF page matches.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-20} {"STATUS",-12} {"TITLE"}");
        Console.WriteLine(new string('-', 110));
        foreach (var page in pages)
        {
            var okf = page.GetProperty("okf");
            var status = DriveTree.Str(okf, "status") ?? "stable";
            if (DriveTree.IsStale(okf, now)) status += ", stale";
            if (DriveTree.Str(okf, "trust") == "human") status += ", verified";
            Console.WriteLine($"{DriveTree.Str(page, "id"),-38} {Truncate(DriveTree.Str(okf, "type") ?? "(no type)", 20),-20} {status,-12} {DriveTree.Str(page, "title")}");
        }
        return 0;
    }

    private static void PrintTree(JsonElement items, int depth)
    {
        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var type = item.GetProperty("type").GetString() ?? "";
            var title = item.GetProperty("title").GetString() ?? "";
            var updated = item.TryGetProperty("updatedAt", out var u) ? u.GetString()?[..10] ?? "" : "";
            var indent = new string(' ', depth * 2);

            Console.WriteLine($"{id,-38} {type,-8} {indent}{Truncate(title, 40 - depth * 2),-40} {updated}");

            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                PrintTree(children, depth + 1);
        }
    }

    public static async Task<int> ReadAsync(SideHubApiClient client, string[] args, bool json)
    {
        var path = GetOption(args, "--path");
        var pageId = path is null ? args.FirstOrDefault(a => !a.StartsWith("--")) : null;
        if (path is not null)
        {
            // An OKF link (`/payments/refund-flow.md`) read from the workspace root
            var tree = await client.GetDriveTreeAsync();
            var found = DriveTree.FindByPath(tree.TryGetProperty("items", out var items) ? items : tree, path);
            if (found is null)
            {
                Console.Error.WriteLine($"No page at {path} (path from the workspace root, made of folder and page titles in lowercase with dashes)");
                return 1;
            }
            if (DriveTree.Str(found.Value, "type") == "folder")
            {
                Console.Error.WriteLine($"{path} is a folder ({DriveTree.Str(found.Value, "id")}): `sidehub-cli drive list` shows its pages");
                return 1;
            }
            pageId = DriveTree.Str(found.Value, "id");
        }
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive read <pageId> | --path </folder/page.md>");
            return 1;
        }

        var result = await client.GetDriveItemAsync(pageId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
        var content = result.TryGetProperty("content", out var c) ? c.GetString() : "";

        Console.WriteLine($"# {title}");
        Console.WriteLine();
        if (!string.IsNullOrEmpty(content))
        {
            Console.WriteLine(content);
        }
        else if (result.TryGetProperty("downloadUrl", out var du) && !string.IsNullOrEmpty(du.GetString()))
        {
            var mime = result.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
            var fileName = result.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
            var size = result.TryGetProperty("fileSize", out var fs) && fs.ValueKind == JsonValueKind.Number
                ? fs.GetInt64().ToString() + " bytes"
                : "unknown size";

            if (IsJsonFile(mime, fileName))
            {
                var raw = await client.GetDriveJsonAsync(pageId);
                Console.WriteLine(PrettyPrintJson(raw));
                return 0;
            }

            var label = string.IsNullOrEmpty(mime) ? size : $"{mime}, {size}";
            Console.WriteLine($"Binary file ({label}). Use `sidehub-cli drive download {pageId}` to fetch it.");
        }
        return 0;
    }

    public static async Task<int> CreateJsonAsync(SideHubApiClient client, string[] args, bool json)
    {
        var unknown = ValidateKnownFlags(args, CreateJsonFlags);
        if (unknown is not null) return unknown.Value;

        var title = GetOption(args, "--title");
        var content = GetOption(args, "--content");
        var filePath = GetOption(args, "--file");
        var parentId = GetOption(args, "--parent");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive create-json --title \"file.json\" [--content '<json>' | --file <path>] [--parent <id>]");
            return 1;
        }

        if (content is null && filePath is null)
        {
            content = "{}";
        }
        else if (content is not null && filePath is not null)
        {
            Console.Error.WriteLine("Error: use either --content or --file, not both");
            return 1;
        }
        else if (filePath is not null)
        {
            var read = TryReadFile(filePath);
            if (read is null) return 1;
            content = read;
        }

        try
        {
            using var _ = JsonDocument.Parse(content!);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: invalid JSON: {ex.Message}");
            return 1;
        }

        var fileName = title.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? title : title + ".json";
        var displayTitle = Path.GetFileNameWithoutExtension(fileName);

        var bytes = Encoding.UTF8.GetBytes(content!);
        var result = await client.UploadJsonBytesAsync(fileName, bytes, parentId, displayTitle);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        Console.WriteLine($"Created JSON file {fileName}: {id}");
        return 0;
    }

    public static async Task<int> QueryAsync(SideHubApiClient client, string[] args, bool json)
    {
        var unknown = ValidateKnownFlags(args, QueryFlags);
        if (unknown is not null) return unknown.Value;

        var itemId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var path = GetOption(args, "--path");
        var raw = args.Contains("--raw");

        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive query <id> --path \"$.foo.bar\" [--raw]");
            Console.Error.WriteLine("       JSONPath: $.field, $['field'], $.array[0], $.array[*], $.array[*].name");
            return 1;
        }

        var content = await client.GetDriveJsonAsync(itemId);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: stored content is not valid JSON: {ex.Message}");
            return 1;
        }

        var results = JsonPathEvaluator.Evaluate(root, path);

        if (json || results.Count != 1)
        {
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r?.DeepClone());
            Console.WriteLine(arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var single = results[0];
        if (raw && single is JsonValue v && v.TryGetValue<string>(out var s))
        {
            Console.WriteLine(s);
        }
        else
        {
            Console.WriteLine(single?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
        }
        return 0;
    }

    public static async Task<int> PatchAsync(SideHubApiClient client, string[] args, bool json)
    {
        var unknown = ValidateKnownFlags(args, PatchFlags);
        if (unknown is not null) return unknown.Value;

        var itemId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(itemId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive patch <id> [--set <pointer>=<value>]* [--delete <pointer>]* [--ops-file <path>]");
            Console.Error.WriteLine("       Pointer is RFC 6901: '/foo/bar', '/items/0', '/items/-' to append");
            Console.Error.WriteLine("       Value is parsed as JSON if possible, otherwise treated as string");
            return 1;
        }

        var opsFile = GetOption(args, "--ops-file");
        string opsJson;

        if (opsFile is not null)
        {
            var content = TryReadFile(opsFile);
            if (content is null) return 1;
            opsJson = content;
        }
        else
        {
            var ops = new JsonArray();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--set")
                {
                    var spec = args[i + 1];
                    var eq = spec.IndexOf('=');
                    if (eq < 0)
                    {
                        Console.Error.WriteLine($"Error: --set expects <pointer>=<value>, got '{spec}'");
                        return 1;
                    }
                    var pointer = spec[..eq];
                    var rawValue = spec[(eq + 1)..];
                    JsonNode? value = ParseValueOrString(rawValue);
                    ops.Add(new JsonObject
                    {
                        ["op"] = "replace",
                        ["path"] = pointer,
                        ["value"] = value
                    });
                }
                else if (args[i] == "--delete")
                {
                    ops.Add(new JsonObject
                    {
                        ["op"] = "remove",
                        ["path"] = args[i + 1]
                    });
                }
            }

            if (ops.Count == 0)
            {
                Console.Error.WriteLine("Error: no operations provided. Use --set, --delete, or --ops-file");
                return 1;
            }

            opsJson = ops.ToJsonString();
        }

        var result = await client.PatchDriveJsonAsync(itemId, opsJson);

        if (json)
        {
            Console.WriteLine(PrettyPrintJson(result));
            return 0;
        }
        Console.WriteLine($"Patched {itemId}");
        Console.WriteLine(PrettyPrintJson(result));
        return 0;
    }

    private static JsonNode? ParseValueOrString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonValue.Create(string.Empty);
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool IsJsonFile(string? mime, string? fileName)
    {
        if (!string.IsNullOrEmpty(mime))
        {
            var m = mime.Trim().ToLowerInvariant();
            if (m == "application/json" || m.EndsWith("+json", StringComparison.Ordinal)) return true;
        }
        if (!string.IsNullOrEmpty(fileName) &&
            string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Builds the path a download is saved to from a file name sent by the server. The name is
    /// untrusted: only its last segment is kept (both '/' and '\' count as separators), and
    /// empty names, '.', '..' and control or invalid characters are refused. The result is
    /// guaranteed to sit directly inside <paramref name="targetDirectory"/>.
    /// </summary>
    public static string ResolveDownloadPath(string targetDirectory, string? serverFileName)
    {
        var raw = serverFileName ?? "";
        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];

        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.Any(char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Refusing to save download: unsafe file name {JsonSerializer.Serialize(raw)} from server. Use --output <file> to choose the name.");
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var fullPath = Path.GetFullPath(Path.Combine(directory, name));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.Equals(Path.GetDirectoryName(fullPath), directory, comparison))
        {
            throw new InvalidOperationException(
                $"Refusing to save download: file name {JsonSerializer.Serialize(raw)} resolves outside {directory}.");
        }

        return fullPath;
    }

    /// <summary>
    /// Writes a download to <paramref name="targetPath"/> without following a link already there: File.Create would
    /// write through a planted <c>report.pdf -> ~/.bashrc</c>. The content goes to a new temporary file next to it
    /// (O_EXCL, which never follows a link), then a rename replaces the entry itself. Returns the bytes written.
    /// </summary>
    public static async Task<long> SaveDownloadAsync(string targetPath, Func<Stream, Task> download)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var tempPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.part");
        try
        {
            long bytesWritten;
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                await download(fs);
                bytesWritten = fs.Length;
            }
            File.Move(tempPath, fullPath, overwrite: true);
            return bytesWritten;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    public static async Task<int> DownloadAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--") && !a.StartsWith("-"));
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive download <pageId> [--output <path>] [--stdout] [--url-only]");
            return 1;
        }

        var output = GetOption(args, "--output") ?? GetOption(args, "-o");
        var toStdout = args.Contains("--stdout");
        var urlOnly = args.Contains("--url-only");

        if (toStdout && output is not null)
        {
            Console.Error.WriteLine("Error: --stdout and --output are mutually exclusive");
            return 1;
        }
        if (urlOnly && (toStdout || output is not null))
        {
            Console.Error.WriteLine("Error: --url-only cannot be combined with --stdout or --output");
            return 1;
        }

        var info = await client.GetDriveDownloadInfoAsync(pageId);

        if (urlOnly)
        {
            if (json)
            {
                var payload = new
                {
                    id = pageId,
                    fileName = info.FileName,
                    mimeType = info.MimeType,
                    fileSize = info.FileSize,
                    downloadUrl = info.DownloadUrl
                };
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine(info.DownloadUrl);
            }
            return 0;
        }

        if (toStdout)
        {
            using var stdout = Console.OpenStandardOutput();
            await client.DownloadToStreamAsync(info.DownloadUrl, stdout);
            return 0;
        }

        string targetPath;
        if (output is null)
        {
            targetPath = ResolveDownloadPath(Directory.GetCurrentDirectory(), info.FileName);
        }
        else if (Directory.Exists(output))
        {
            targetPath = ResolveDownloadPath(output, info.FileName);
        }
        else
        {
            targetPath = output;
            var parent = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        }

        var bytesWritten = await SaveDownloadAsync(targetPath, fs => client.DownloadToStreamAsync(info.DownloadUrl, fs));

        var absolutePath = Path.GetFullPath(targetPath);

        if (json)
        {
            var payload = new
            {
                id = pageId,
                fileName = info.FileName,
                mimeType = info.MimeType,
                fileSize = info.FileSize,
                downloadUrl = info.DownloadUrl,
                savedTo = absolutePath,
                bytesWritten
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Saved {bytesWritten} bytes to {absolutePath}");
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var unknown = ValidateKnownFlags(args, CreateFlags);
        if (unknown is not null) return unknown.Value;

        var title = GetOption(args, "--title");
        var content = GetOption(args, "--content");
        var filePath = GetOption(args, "--file");
        var parentId = GetOption(args, "--parent");
        var type = GetOption(args, "--type");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive create --title \"...\" [--content \"...\" | --file <path>] [--parent <id>] [--type page|spreadsheet|folder]");
            return 1;
        }

        if (type is not null && type != "page" && type != "spreadsheet" && type != "folder")
        {
            Console.Error.WriteLine("Error: --type must be 'page', 'spreadsheet', or 'folder'");
            return 1;
        }

        if (filePath is not null)
        {
            if (content is not null)
            {
                Console.Error.WriteLine("Error: use either --content or --file, not both");
                return 1;
            }
            var fileContent = TryReadFile(filePath);
            if (fileContent is null) return 1;
            content = fileContent;
        }

        if (content is not null && content.Length > 100 * 1024)
        {
            Console.Error.WriteLine("Error: content exceeds 100KB limit");
            return 1;
        }

        if (content is not null && (type is null || type == "page")) WarnOkf(content);

        var result = await client.CreateDriveItemAsync(title, content, parentId, type);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        var label = type == "spreadsheet" ? "spreadsheet" : "page";
        Console.WriteLine($"Created {label}: {id}");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var unknown = ValidateKnownFlags(args, UpdateFlags);
        if (unknown is not null) return unknown.Value;

        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var title = GetOption(args, "--title");
        var content = GetOption(args, "--content");
        var filePath = GetOption(args, "--file");

        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive update <pageId> [--title \"...\"] [--content \"...\" | --file <path>]");
            return 1;
        }

        if (filePath is not null)
        {
            if (content is not null)
            {
                Console.Error.WriteLine("Error: use either --content or --file, not both");
                return 1;
            }
            var fileContent = TryReadFile(filePath);
            if (fileContent is null) return 1;
            content = fileContent;
        }

        if (content is not null && content.Length > 100 * 1024)
        {
            Console.Error.WriteLine("Error: content exceeds 100KB limit");
            return 1;
        }

        if (content is not null) WarnOkf(content);

        var result = await client.UpdateDriveItemAsync(pageId, title, content);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated page: {pageId}");
        return 0;
    }

    public static async Task<int> SearchAsync(SideHubApiClient client, string[] args, bool json)
    {
        var query = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(query))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive search <query>");
            return 1;
        }

        var result = await client.GetDriveTreeAsync();

        if (!result.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            Console.WriteLine("No items in drive.");
            return 0;
        }

        var matches = new List<(string Id, string Type, string Title, string Updated)>();
        CollectMatches(items, query, matches);

        if (matches.Count == 0)
        {
            Console.WriteLine($"No pages matching \"{query}\".");
            return 0;
        }

        if (json)
        {
            var jsonArray = matches.Select(m => new { id = m.Id, type = m.Type, title = m.Title, updatedAt = m.Updated });
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonArray, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-8} {"TITLE",-40} {"UPDATED"}");
        Console.WriteLine(new string('-', 110));
        foreach (var m in matches)
            Console.WriteLine($"{m.Id,-38} {m.Type,-8} {Truncate(m.Title, 40),-40} {m.Updated}");

        return 0;
    }

    private static void CollectMatches(JsonElement items, string query, List<(string Id, string Type, string Title, string Updated)> matches)
    {
        foreach (var item in items.EnumerateArray())
        {
            var title = item.GetProperty("title").GetString() ?? "";
            if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var id = item.GetProperty("id").GetString() ?? "";
                var type = item.GetProperty("type").GetString() ?? "";
                var updated = item.TryGetProperty("updatedAt", out var u) ? u.GetString()?[..10] ?? "" : "";
                matches.Add((id, type, title, updated));
            }

            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                CollectMatches(children, query, matches);
        }
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive delete <id> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes") && !ConfirmDelete($"drive item {pageId}"))
            return 1;

        await client.DeleteDriveItemAsync(pageId);

        if (json) Console.WriteLine($"{{\"deleted\":\"{pageId}\"}}");
        else Console.WriteLine($"Deleted drive item: {pageId}");
        return 0;
    }

    public static async Task<int> MoveAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var newParent = GetOption(args, "--parent");
        var afterSibling = GetOption(args, "--after");

        if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(newParent))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive move <id> --parent <newParentId|root> [--after <siblingId>]");
            Console.Error.WriteLine("       Use --parent root to move to the workspace root.");
            return 1;
        }

        // "root" sentinel → null parent (workspace root)
        var resolvedParent = string.Equals(newParent, "root", StringComparison.OrdinalIgnoreCase) ? null : newParent;

        var result = await client.MoveDriveItemAsync(pageId, resolvedParent, afterSibling);

        if (json && result.ValueKind != JsonValueKind.Undefined)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        Console.WriteLine($"Moved {pageId} to parent {resolvedParent ?? "<root>"}");
        return 0;
    }

    public static async Task<int> MkdirAsync(SideHubApiClient client, string[] args, bool json)
    {
        var title = GetOption(args, "--title");
        var parentId = GetOption(args, "--parent");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive mkdir --title \"...\" [--parent <id>]");
            return 1;
        }

        var result = await client.CreateDriveItemAsync(title, content: null, parentId, type: "folder");

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        Console.WriteLine($"Created folder: {id}");
        return 0;
    }

    public static async Task<int> UploadAsync(SideHubApiClient client, string[] args, bool json)
    {
        var localPath = FirstPositional(args, "--parent", "--name", "--title");
        var parentId = GetOption(args, "--parent");
        var title = GetOption(args, "--name") ?? GetOption(args, "--title");
        var skipValidation = args.Contains("--no-validate");
        var markdown = args.Contains("--markdown");

        if (string.IsNullOrEmpty(localPath))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive upload <localPath> [--parent <id>] [--name \"...\"] [--markdown] [--no-validate]");
            return 1;
        }
        if (!File.Exists(localPath))
        {
            Console.Error.WriteLine($"Error: file not found: {localPath}");
            return 1;
        }

        if (!skipValidation &&
            string.Equals(Path.GetExtension(localPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = File.OpenRead(localPath);
                using var _ = await JsonDocument.ParseAsync(fs);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Error: invalid JSON in {localPath}: {ex.Message}");
                Console.Error.WriteLine("       Use --no-validate to upload anyway.");
                return 1;
            }
        }

        var result = await client.UploadFileAsync(localPath, parentId, title);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        var fileName = result.TryGetProperty("fileName", out var f) ? f.GetString() : Path.GetFileName(localPath);
        if (markdown)
        {
            var mimeType = result.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
            if (mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                Console.Error.WriteLine($"Warning: {fileName} is not an image ({mimeType ?? "unknown type"}): a page shows only images, it will appear as its caption.");
            // Stdout is the line alone, ready to paste into a page
            Console.WriteLine(ImageMarkdown(title ?? Path.GetFileNameWithoutExtension(fileName) ?? "", id ?? ""));
            return 0;
        }
        Console.WriteLine($"Uploaded {fileName}: {id}");
        return 0;
    }

    /// <summary>
    /// A Markdown image that shows a Drive file in a page: <c>![caption](drive:&lt;id&gt;)</c>.
    /// The page resolves the stable <c>drive:</c> reference to a fresh URL each time it is opened.
    /// </summary>
    internal static string ImageMarkdown(string caption, string id)
    {
        var alt = string.Concat(caption.ReplaceLineEndings(" ").Select(c => c is '[' or ']' or '\\' ? $"\\{c}" : c.ToString()));
        return $"![{alt.Trim()}](drive:{id})";
    }

    /// <summary>First argument that is neither a flag nor the value of one of <paramref name="valueOptions"/>.</summary>
    private static string? FirstPositional(string[] args, params string[] valueOptions)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (valueOptions.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith('-')) return args[i];
        }
        return null;
    }

    public static async Task<int> RecentAsync(SideHubApiClient client, string[] args, bool json)
    {
        int? limit = null;
        var limitArg = GetOption(args, "--limit");
        if (!string.IsNullOrEmpty(limitArg) && int.TryParse(limitArg, out var n)) limit = n;

        var result = await client.GetRecentDriveItemsAsync(limit);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            Console.WriteLine("No recent items.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-8} {"TITLE",-40} {"UPDATED"}");
        Console.WriteLine(new string('-', 110));
        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
            var updated = item.TryGetProperty("updatedAt", out var u) ? u.GetString()?[..10] ?? "" : "";
            Console.WriteLine($"{id,-38} {type,-8} {Truncate(title, 40),-40} {updated}");
        }
        return 0;
    }

    public static async Task<int> ArchivedAsync(SideHubApiClient client, string[] args, bool json)
    {
        var result = await client.GetArchivedDriveItemsAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No archived items.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"TYPE",-12} {"TITLE",-40} {"ARCHIVED"}");
        Console.WriteLine(new string('-', 110));
        foreach (var item in result.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
            var archived = item.TryGetProperty("archivedAt", out var a) ? a.GetString()?[..10] ?? "" : "";
            Console.WriteLine($"{id,-38} {type,-12} {Truncate(title, 40),-40} {archived}");
        }
        Console.WriteLine();
        Console.WriteLine("Bring one back with: sidehub-cli drive restore <id>");
        return 0;
    }

    public static async Task<int> RestoreAsync(SideHubApiClient client, string[] args, bool json)
    {
        var itemId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(itemId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli drive restore <id>");
            return 1;
        }

        await client.RestoreDriveItemAsync(itemId);
        if (json) Console.WriteLine($"{{\"restored\":\"{itemId}\"}}");
        else Console.WriteLine($"Restored drive item: {itemId}");
        return 0;
    }

    public static async Task<int> UsageAsync(SideHubApiClient client, string[] args, bool json)
    {
        var result = await client.GetStorageUsageAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine(SideHubApiClient.Serialize(result));
        return 0;
    }

    private static bool ConfirmDelete(string label)
    {
        Console.Write($"Delete {label}? Type 'yes' to confirm: ");
        var input = Console.ReadLine();
        if (!string.Equals(input?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Aborted.");
            return false;
        }
        return true;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static readonly HashSet<string> CreateFlags = new()
    {
        "--title", "--content", "--file", "--parent", "--type", "--json"
    };

    private static readonly HashSet<string> UpdateFlags = new()
    {
        "--title", "--content", "--file", "--json"
    };

    private static readonly HashSet<string> CreateJsonFlags = new()
    {
        "--title", "--content", "--file", "--parent", "--json"
    };

    private static readonly HashSet<string> QueryFlags = new()
    {
        "--path", "--raw", "--json"
    };

    private static readonly HashSet<string> PatchFlags = new()
    {
        "--set", "--delete", "--ops-file", "--json"
    };

    private static int? ValidateKnownFlags(string[] args, HashSet<string> known)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            if (!known.Contains(a))
            {
                Console.Error.WriteLine($"Error: unknown flag '{a}'");
                return 1;
            }
        }
        return null;
    }

    /// <summary>
    /// A page that opens on a YAML front matter is meant as an OKF concept: says on stderr what keeps it from reading
    /// as one (no `type`, invalid YAML, a field written differently from the spec). The page is written anyway.
    /// </summary>
    internal static void WarnOkf(string content)
    {
        if (OkfConcept.Check(content) is not { } check) return;
        foreach (var problem in check.Errors.Concat(check.Warnings))
            Console.Error.WriteLine($"Warning (OKF): {problem}");
    }

    private static string? TryReadFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Error: file not found: {path}");
            return null;
        }
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: cannot read file '{path}': {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
