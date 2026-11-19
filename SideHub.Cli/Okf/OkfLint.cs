using System.Text.Json;
using System.Text.RegularExpressions;

namespace SideHub.Cli.Okf;

/// <summary>
/// <c>sidehub-cli okf lint &lt;folder&gt;</c>: checks an OKF bundle written in a repository, offline (no SideHub session
/// needed). Errors are what OKF §11 calls non-conformant: a concept without a readable front matter or without
/// <c>type</c>, an <c>index.md</c> with a front matter (only <c>okf_version</c> at the bundle root), a <c>log.md</c>
/// date heading that is not YYYY-MM-DD. Warnings: fields written differently from the spec, citations without a source,
/// links to a concept that does not exist (allowed by OKF, listed so the agent can tell a typo from a page to write).
/// Exits 1 when there is an error.
/// </summary>
public static partial class OkfLint
{
    public const string Domain = "okf";
    public const string Action = "lint";

    public sealed record Finding(string File, string Message);

    public sealed record Report(int Concepts, List<Finding> Errors, List<Finding> Warnings);

    public static int Run(string[] args)
    {
        var json = args.Contains("--json");
        var folder = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (folder is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli okf lint <folder> [--json]");
            return 1;
        }
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"Error: folder not found: {folder}");
            return 1;
        }

        var report = Lint(folder);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                concepts = report.Concepts,
                errors = report.Errors.Select(f => new { file = f.File, message = f.Message }),
                warnings = report.Warnings.Select(f => new { file = f.File, message = f.Message }),
            }));
        }
        else
        {
            foreach (var f in report.Errors) Console.WriteLine($"error    {f.File}: {f.Message}");
            foreach (var f in report.Warnings) Console.WriteLine($"warning  {f.File}: {f.Message}");
            Console.WriteLine($"{report.Concepts} concept(s), {report.Errors.Count} error(s), {report.Warnings.Count} warning(s)");
        }
        return report.Errors.Count > 0 ? 1 : 0;
    }

    public static Report Lint(string folder)
    {
        var root = Path.GetFullPath(folder);
        var errors = new List<Finding>();
        var warnings = new List<Finding>();
        var concepts = 0;

        foreach (var path in MarkdownFiles(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var text = File.ReadAllText(path);
            var name = Path.GetFileName(path).ToLowerInvariant();
            var split = OkfConcept.SplitFrontMatter(text);
            var body = split?.Body ?? text;

            if (name == "index.md")
                CheckIndex(relative, split, errors);
            else if (name == "log.md")
                CheckLog(relative, body, errors);
            else
            {
                concepts++;
                if (split is null)
                    errors.Add(new(relative, "no front matter: an OKF concept starts with a YAML block between --- lines, with at least `type`"));
                else
                {
                    var (e, w) = OkfConcept.CheckFrontMatter(split.Value.Yaml, split.Value.Body);
                    errors.AddRange(e.Select(m => new Finding(relative, m)));
                    warnings.AddRange(w.Select(m => new Finding(relative, m)));
                }
            }

            foreach (var target in ConceptLinks(body))
            {
                var resolved = Resolve(root, Path.GetDirectoryName(path)!, target);
                if (resolved is null)
                    warnings.Add(new(relative, $"the link {target} points outside the bundle"));
                else if (!(target.TrimEnd().EndsWith('/') ? Directory.Exists(resolved) : File.Exists(resolved)))
                    warnings.Add(new(relative, $"the link {target} names no concept of the bundle (allowed: knowledge not written yet)"));
            }
        }
        return new Report(concepts, errors, warnings);
    }

    private static void CheckIndex(string relative, (string Yaml, string Body)? split, List<Finding> errors)
    {
        if (split is null) return;
        var atRoot = !relative.Contains('/');
        var mapping = OkfConcept.ReadMapping(split.Value.Yaml, out _);
        var onlyVersion = mapping is not null && mapping.Children.Keys.All(k => k.ToString() == "okf_version");
        if (!atRoot || !onlyVersion)
            errors.Add(new(relative, atRoot
                ? "index.md carries a front matter: only `okf_version` is allowed there, at the bundle root"
                : "index.md carries a front matter: an index has none (only the bundle root may declare `okf_version`)"));
    }

    [GeneratedRegex(@"^##\s+(.*?)\s*$")]
    private static partial Regex LogHeading();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    private static void CheckLog(string relative, string body, List<Finding> errors)
    {
        foreach (var line in OkfConcept.OutsideCode(body))
        {
            var heading = LogHeading().Match(line.TrimEnd('\r'));
            if (heading.Success && !IsoDate().IsMatch(heading.Groups[1].Value))
                errors.Add(new(relative, $"log.md date heading \"## {heading.Groups[1].Value}\" is not YYYY-MM-DD"));
        }
    }

    [GeneratedRegex(@"\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex MarkdownLink();

    /// <summary>Targets of the links of a body that name a concept or a folder of the bundle (`x.md`, `dir/`).</summary>
    public static IEnumerable<string> ConceptLinks(string body)
    {
        foreach (var line in OkfConcept.OutsideCode(body))
            foreach (Match m in MarkdownLink().Matches(line))
            {
                var target = m.Groups[1].Value;
                var path = Regex.Replace(target, @"[?#].*$", "");
                if (path.Length == 0 || Regex.IsMatch(path, "^[a-zA-Z][a-zA-Z0-9+.-]*:") || path.StartsWith("//")) continue;
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith('/')) yield return target;
            }
    }

    /// <summary>Where a link leads on disk: from the bundle root when it starts with '/', else from the file's folder. Null outside the bundle.</summary>
    public static string? Resolve(string root, string fileDir, string target)
    {
        var path = Uri.UnescapeDataString(Regex.Replace(target, @"[?#].*$", ""));
        var full = Path.GetFullPath(path.StartsWith('/') ? Path.Combine(root, path.TrimStart('/')) : Path.Combine(fileDir, path));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full == root || full.StartsWith(rootWithSep, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>The .md files of a bundle, hidden folders (.git, .claims) and node_modules left out.</summary>
    private static IEnumerable<string> MarkdownFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && name != "node_modules") pending.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
                if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) yield return file;
        }
    }
}
