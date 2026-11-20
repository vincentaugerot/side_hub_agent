using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Cli.Launch;

namespace SideHub.Cli.Commands;

/// <summary>
/// The scripted tests of a run (SideHub doc 73): the Playwright commands that play them, and what is sent of their
/// reports. SideHub reads a report itself (file and grep to test, <c>test.step</c> to steps): nothing here says whether
/// a test passed.
/// </summary>
public static class ScriptedTests
{
    /// <summary>Set for a script: the JSON file it writes the values it produces to.</summary>
    public const string ValuesFileVariable = "SIDEHUB_TEST_VALUES_FILE";

    /// <summary>Where Playwright's JSON reporter writes (1.58: <c>resolveOutputFile</c> in reporters/base.js).</summary>
    public const string ReportFileVariable = "PLAYWRIGHT_JSON_OUTPUT_FILE";

    /// <summary>The same for an older Playwright, which only reads a name.</summary>
    public const string LegacyReportFileVariable = "PLAYWRIGHT_JSON_OUTPUT_NAME";

    /// <summary>Largest image sent to the Drive for a failure.</summary>
    public const long MaxImageBytes = 6 * 1024 * 1024;

    private static readonly string[] ConfigNames =
    [
        "playwright.config.ts", "playwright.config.js", "playwright.config.mjs",
        "playwright.config.cjs", "playwright.config.mts", "playwright.config.cts",
    ];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // ----- The plan -----

    /// <param name="Variable">The variable the script reads the value in.</param>
    /// <param name="Source">execution, or fallback: the secret of the environment named by <paramref name="Fallback"/>.</param>
    public sealed record Use(string? Name, string? Variable, string? Source, string? Fallback);

    public sealed record Produce(string? Name, bool Secret);

    public sealed record Test(string? Key, string? File, string? Grep, IReadOnlyList<Use>? Uses, IReadOnlyList<Produce>? Produces);

    /// <summary>What the run plays (<c>GET api/test-executions/{id}/script-plan</c>).</summary>
    /// <param name="Recheck">The scripts failed earlier and are played once more, without retry.</param>
    public sealed record Plan(int Number, bool Recheck, IReadOnlyList<Test>? Tests)
    {
        public IReadOnlyList<Test> Scripts => Tests?.Where(t => !string.IsNullOrWhiteSpace(t.File)).ToList() ?? [];

        /// <summary>Whether a value the scripts set is a secret; false for one no test of the run declares.</summary>
        public bool IsSecret(string name) => (Tests ?? [])
            .SelectMany(t => t.Produces ?? [])
            .Any(p => p.Secret && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static Plan ReadPlan(JsonElement plan) =>
        plan.Deserialize<Plan>(JsonSerializerOptions.Web) ?? new Plan(0, false, []);

    // ----- The commands -----

    /// <summary>The tests one Playwright command plays.</summary>
    /// <param name="Folder">Where it runs: the folder of the Playwright project.</param>
    /// <param name="Files">Their files, from that folder.</param>
    /// <param name="Grep">The <c>--grep</c> of the command, or null.</param>
    public sealed record Group(string Folder, IReadOnlyList<string> Files, string? Grep);

    /// <summary>The tests by Playwright project, in the order of the plan.</summary>
    /// <param name="root">The folder the run started in: the repository of the tests.</param>
    public static IReadOnlyList<Group> Groups(IEnumerable<Test> tests, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return tests
            .Select(t => (Test: t, Path: Path.GetFullPath(Path.Combine(root, t.File ?? ""))))
            .GroupBy(t => ProjectFolder(t.Path, root))
            .Select(g => new Group(
                g.Key,
                g.Select(t => Path.GetRelativePath(g.Key, t.Path).Replace(Path.DirectorySeparatorChar, '/')).Distinct().ToList(),
                JoinGreps(g.Select(t => t.Test.Grep).ToList())))
            .ToList();
    }

    /// <summary>
    /// The nearest folder with a Playwright configuration, from the folder of <paramref name="file"/> up to
    /// <paramref name="root"/>; <paramref name="root"/> when there is none.
    /// </summary>
    public static string ProjectFolder(string file, string root)
    {
        var inside = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        for (var folder = Path.GetDirectoryName(file);
             folder is not null && (folder.Equals(root, PathComparison) || folder.StartsWith(inside, PathComparison));
             folder = Path.GetDirectoryName(folder))
        {
            if (ConfigNames.Any(name => File.Exists(Path.Combine(folder, name))))
                return folder;
        }
        return root;
    }

    /// <summary>
    /// The <c>--grep</c> of a command: each grep of its tests, when every one of them has one. A test without a grep is
    /// its whole file: the command then takes none, and SideHub finds each test by its own grep.
    /// </summary>
    public static string? JoinGreps(IReadOnlyList<string?> greps) =>
        greps.Count > 0 && greps.All(g => !string.IsNullOrEmpty(g))
            ? string.Join("|", greps.Distinct().Select(g => $"({g})"))
            : null;

    /// <summary>The arguments of npx for a group, one per item: a title or a grep never goes through a shell.</summary>
    public static IReadOnlyList<string> Arguments(Group group, bool recheck)
    {
        List<string> arguments = ["playwright", "test", .. group.Files.Select(FileFilter), "--reporter=list,json"];
        if (group.Grep is not null)
            arguments.AddRange(["--grep", group.Grep]);
        if (recheck)
            arguments.Add("--retries=0");
        return arguments;
    }

    /// <summary>
    /// Playwright reads a file argument as a regular expression: what would change its meaning is escaped (a folder
    /// named <c>[id]</c> or <c>(app)</c>). A dot stays: it matches itself too.
    /// </summary>
    private static string FileFilter(string file) =>
        string.Concat(file.Select(c => @"\^$*+?()[]{}|".Contains(c) ? $@"\{c}" : c.ToString()));

    /// <summary>A command as a person would type it, for the terminal: its environment is never shown.</summary>
    public static string Display(IEnumerable<string> arguments) =>
        string.Join(" ", new[] { "npx" }.Concat(arguments).Select(a => a.Any(c => char.IsWhiteSpace(c) || @"|&;<>()[]{}$`""'*?\^".Contains(c))
            ? "'" + a.Replace("'", @"'\''") + "'"
            : a));

    /// <summary>
    /// What a script reads a value in, when no test of the execution produced it: the variable of the value takes the
    /// secret of the environment it falls back on. A variable already set is left alone.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Fallbacks(IEnumerable<Test> tests, Func<string, string?> environment)
    {
        var variables = new Dictionary<string, string>();
        foreach (var use in tests.SelectMany(t => t.Uses ?? []))
        {
            if (use.Source != "fallback" || string.IsNullOrEmpty(use.Variable) || string.IsNullOrEmpty(use.Fallback))
                continue;
            if (!string.IsNullOrEmpty(environment(use.Variable)) || environment(use.Fallback) is not { Length: > 0 } secret)
                continue;
            variables[use.Variable] = secret;
        }
        return variables;
    }

    /// <summary>
    /// npx and the arguments to put before its own. On Windows npx is a <c>.cmd</c>, which cmd.exe runs and parses again
    /// (a grep holds <c>|</c> and <c>^</c>): node runs its script instead, when it is where Node.js installs it.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> LeadingArguments) Npx(string? pathVariable)
    {
        if (!OperatingSystem.IsWindows())
            return ("npx", []);
        if (RealCli.FindWindows("npx", pathVariable, []) is not { } npx)
            return ("npx.cmd", []);
        var folder = Path.GetDirectoryName(npx)!;
        var script = Path.Combine(folder, "node_modules", "npm", "bin", "npx-cli.js");
        if (!npx.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
            return (npx, []);
        var node = Path.Combine(folder, "node.exe");
        return (File.Exists(node) ? node : "node", [script]);
    }

    /// <summary>Starts a command and waits for it; throws <see cref="System.ComponentModel.Win32Exception"/> when it cannot start.</summary>
    public delegate int Starter(ProcessStartInfo startInfo);

    /// <summary>The output of the command goes to the terminal as it comes.</summary>
    public static int Start(ProcessStartInfo startInfo)
    {
        // Ctrl+C reaches every process of the terminal: Playwright stops and writes its report, which is still sent.
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    // ----- The reports -----

    /// <summary>A report as Playwright wrote it, or null when the file is missing or is not one.</summary>
    public static JsonObject? ReadReport(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// One report for the run: the first one, with the suites and the errors of the next ones, then
    /// <paramref name="errors"/> (a command that left no report), each as Playwright writes an error.
    /// </summary>
    public static JsonObject Merge(IReadOnlyList<JsonObject> reports, IEnumerable<string> errors)
    {
        var merged = reports.Count > 0 ? (JsonObject)reports[0].DeepClone() : new JsonObject();
        var suites = List(merged, "suites");
        var all = List(merged, "errors");
        foreach (var report in reports.Skip(1))
        {
            foreach (var suite in Items(report, "suites"))
                suites.Add(suite?.DeepClone());
            foreach (var error in Items(report, "errors"))
                all.Add(error?.DeepClone());
        }
        foreach (var error in errors)
            all.Add(new JsonObject { ["message"] = error });
        return merged;
    }

    /// <summary>
    /// Drops what SideHub does not read and what weighs: the output of the tests (<c>stdout</c>, <c>stderr</c>) and the
    /// attachments written in the report itself (<c>body</c>, base64). The rest stays as Playwright wrote it.
    /// </summary>
    public static void Slim(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            item.Remove("stdout");
            item.Remove("stderr");
            foreach (var attachment in Items(item, "attachments").OfType<JsonObject>())
                attachment.Remove("body");
            foreach (var (_, child) in item.ToList())
                Slim(child);
        }
        else if (node is JsonArray items)
        {
            foreach (var child in items)
                Slim(child);
        }
    }

    /// <summary>
    /// The image of each test that neither passed nor was skipped, by its path as the report writes it: the first image
    /// Playwright attached as a file to its last result (the earlier ones are retries, and SideHub shows that image
    /// only), when <paramref name="usable"/> says it can be sent.
    /// </summary>
    public static IReadOnlyList<string> FailureImages(JsonObject report, Func<string, bool> usable)
    {
        var images = new List<string>();
        foreach (var suite in Items(report, "suites"))
            Collect(suite);
        return images.Distinct().Where(usable).ToList();

        void Collect(JsonNode? suite)
        {
            foreach (var test in Items(suite, "specs").SelectMany(s => Items(s, "tests")))
            {
                var result = Items(test, "results").LastOrDefault();
                if (result is null || Text(result, "status") is "passed" or "skipped")
                    continue;
                var image = Items(result, "attachments")
                    .Where(a => Text(a, "contentType")?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(a => Text(a, "path"))
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path));
                if (image is not null)
                    images.Add(image);
            }
            foreach (var child in Items(suite, "suites"))
                Collect(child);
        }
    }

    /// <summary>A file that exists, of <see cref="MaxImageBytes"/> at most.</summary>
    public static bool IsUsableImage(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= MaxImageBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The name of a capture in the Drive: Playwright names the folder of a result after its test.</summary>
    public static string CaptureTitle(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrEmpty(folder) ? Path.GetFileName(path) : $"{folder} {Path.GetFileName(path)}";
    }

    // ----- The values -----

    /// <summary>
    /// The values a script wrote to its values file: a JSON object, name to text (a number or a boolean as written).
    /// Empty without a file; <paramref name="problem"/> says why a file was not read.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadValues(string path, out string? problem)
    {
        problem = null;
        var values = new Dictionary<string, string>();
        if (!File.Exists(path))
            return values;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject written)
            {
                problem = "it is not a JSON object (name to value)";
                return values;
            }
            foreach (var (name, value) in written)
            {
                if (value is JsonValue scalar && scalar.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    values[name] = scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : scalar.ToJsonString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = ex is JsonException ? "it is not valid JSON" : ex.Message;
        }
        return values;
    }

    // ----- Helpers -----

    private static JsonArray List(JsonObject item, string name)
    {
        if (item[name] is JsonArray list)
            return list;
        list = new JsonArray();
        item[name] = list;
        return list;
    }

    private static IEnumerable<JsonNode?> Items(JsonNode? node, string name) =>
        node is JsonObject item && item[name] is JsonArray list ? list.ToList() : [];

    private static string? Text(JsonNode? node, string name) =>
        node is JsonObject item && item[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
