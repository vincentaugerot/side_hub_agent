using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// End-to-end tests of the workspace (SideHub doc 73). Writing them: a test is a file (<c>sidehub.qa/v1</c>: given,
/// steps with when / then, values used and produced), created active from a terminal its user opened, proposed as a
/// draft from a run or a queued task. Playing them: <c>test run</c> launches an execution; inside a run of an
/// execution, <c>test result</c> and <c>test value</c> report each step and each value as the tests are played.
/// </summary>
public static class TestCommands
{
    /// <summary>Set by the backend in the runs of a test execution.</summary>
    public const string ExecutionVariable = "SIDEHUB_TEST_EXECUTION_ID";

    private static readonly string[] Statuses = ["draft", "active", "quarantined", "archived"];
    private static readonly string[] StepStatuses = ["passed", "failed", "inconclusive"];
    private static readonly string[] ThenStatuses = ["verified", "failed", "no-evidence"];
    private static readonly string[] Clis = ["claude", "codex", "agy", "copilot"];

    // ----- Writing tests -----

    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var status = GetOption(args, "--status");
        if (status is not null && !Statuses.Contains(status))
        {
            Console.Error.WriteLine("Usage: sidehub-cli test list [--repository <name|id>] [--feature \"...\"] [--status draft|active|quarantined|archived]");
            return 1;
        }

        var repository = GetOption(args, "--repository");
        var tests = await client.ListTestsAsync(Guid.TryParse(repository, out _) ? repository : null, GetOption(args, "--feature"), status);
        // By name: the tests name their repositories, no other call is needed.
        var listed = tests.ValueKind == JsonValueKind.Array
            ? tests.EnumerateArray()
                .Where(t => repository is null || Guid.TryParse(repository, out _)
                    || Items(t, "repositories").Any(r => string.Equals(Prop(r, "name"), repository, StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : [];

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(listed, SerializerOptions));
            return 0;
        }
        if (listed.Count == 0)
        {
            Console.WriteLine("No tests found.");
            return 0;
        }

        Console.WriteLine($"{"KEY",-34} {"STATUS",-12} {"PRIORITY",-9} {"MODE",-7} {"STEPS",-6} {"LAST",-13} {"FEATURE",-18} {"TITLE"}");
        Console.WriteLine(new string('-', 150));
        foreach (var test in listed)
        {
            var history = Items(test, "history");
            var last = history.Count > 0 ? Prop(history[^1], "status") ?? "-" : "-";
            Console.WriteLine($"{Truncate(Prop(test, "key") ?? "", 34),-34} {Prop(test, "status"),-12} {Prop(test, "priority"),-9} {Prop(test, "mode"),-7} " +
                $"{Items(test, "steps").Count,-6} {last,-13} {Truncate(Prop(test, "feature") ?? "", 18),-18} {Truncate(Prop(test, "title") ?? "", 60)}");
        }
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reference = Positionals(args).FirstOrDefault();
        if (reference is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli test get <key|id>");
            return 1;
        }

        var test = await client.GetTestAsync(await ResolveTestIdAsync(client, reference));
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(test));
            return 0;
        }

        Console.WriteLine($"Test:         {Prop(test, "key")} ({Prop(test, "id")})");
        Console.WriteLine($"Title:        {Prop(test, "title")}");
        Console.WriteLine($"Feature:      {Prop(test, "feature")}");
        Console.WriteLine($"Status:       {Prop(test, "status")}, version {Prop(test, "version")}");
        Console.WriteLine($"Priority:     {Prop(test, "priority")}");
        Console.WriteLine($"Played by:    {(Prop(test, "mode") == "script" ? "a script" : "an agent")}");
        Console.WriteLine($"Repositories: {string.Join(", ", Items(test, "repositories").Select(r => Prop(r, "name")))} (the first one is its own)");
        Console.WriteLine($"Writes data:  {(Bool(test, "writes") ? "yes" : "no")}");
        PrintList("Given", Items(test, "given").Select(g => g.GetString() ?? ""));
        var needs = Items(test, "needs");
        if (needs.Count > 0)
        {
            Console.WriteLine("Uses:");
            foreach (var need in needs)
            {
                var producer = need.TryGetProperty("producer", out var p) && p.ValueKind == JsonValueKind.Object ? $", set by '{Prop(p, "key")}'" : ", no test produces it";
                var fallback = Prop(need, "fallback") is { } f ? $", fallback {f}" : ", no fallback";
                Console.WriteLine($"  - {Prop(need, "name")}{producer}{fallback}");
            }
        }
        var steps = Items(test, "steps");
        for (var i = 0; i < steps.Count; i++)
        {
            Console.WriteLine();
            Console.WriteLine($"Step {i + 1}: {Prop(steps[i], "title")}");
            Console.WriteLine($"  When: {Prop(steps[i], "when")}");
            Console.WriteLine("  Then:");
            foreach (var then in Items(steps[i], "then"))
                Console.WriteLine($"    - {then.GetString()}");
            var produces = Items(steps[i], "produces");
            if (produces.Count > 0)
                Console.WriteLine("  Produces: " + string.Join(", ", produces.Select(p => Bool(p, "secret") ? $"{Prop(p, "name")} (secret)" : Prop(p, "name"))));
        }
        Console.WriteLine();
        if (Prop(test, "data") is { } data) Console.WriteLine($"Data:         {data}");
        if (Prop(test, "cleanup") is { } cleanup) Console.WriteLine($"Cleanup:      {cleanup}");
        var outcomes = Items(test, "history");
        if (outcomes.Count > 0)
            Console.WriteLine("Last runs:    " + string.Join(", ", outcomes.Select(o => $"#{Prop(o, "number")} {Prop(o, "status")}")));
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (await ReadFileAsync(GetOption(args, "--file"), "sidehub-cli test create --file <test.yaml>") is not { } yaml)
            return 1;

        // A run, or a terminal SideHub opened on its own for a queued task, only proposes: the test is a draft until a
        // member validates it. A terminal its user opened creates it active, as them.
        var identity = await client.GetIdentityAsync();
        var unattended = Prop(identity, "token") switch
        {
            "run" => true,
            "terminal" => !(identity.TryGetProperty("actingUser", out var user) && user.ValueKind == JsonValueKind.Object),
            _ => false,
        };

        var test = await client.CreateTestAsync(yaml, unattended);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(test));
            return 0;
        }
        Console.WriteLine(Prop(test, "status") == "draft"
            ? $"Proposed test: {Prop(test, "key")} ({Prop(test, "id")}). It is a draft: a member validates it on the Tests page before it is played."
            : $"Created test: {Prop(test, "key")} ({Prop(test, "id")}), {Prop(test, "status")}");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reference = Positionals(args, "--file").FirstOrDefault();
        const string usage = "sidehub-cli test update <key|id> --file <test.yaml>";
        if (reference is null)
        {
            Console.Error.WriteLine($"Usage: {usage}");
            return 1;
        }
        if (await ReadFileAsync(GetOption(args, "--file"), usage) is not { } yaml)
            return 1;

        var test = await client.UpdateTestAsync(await ResolveTestIdAsync(client, reference), yaml);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(test));
            return 0;
        }
        Console.WriteLine($"Updated test: {Prop(test, "key")}, now version {Prop(test, "version")}");
        return 0;
    }

    public static async Task<int> ArchiveAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reference = Positionals(args).FirstOrDefault();
        if (reference is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli test archive <key|id> [--yes]");
            return 1;
        }
        if (!args.Contains("--yes") && !Confirm($"Archive the test '{reference}'? It is no longer played."))
            return 1;

        var test = await client.ChangeTestStatusAsync(await ResolveTestIdAsync(client, reference), "archive");
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(test));
            return 0;
        }
        Console.WriteLine($"Archived test: {Prop(test, "key")}");
        return 0;
    }

    public static async Task<int> ImportAsync(SideHubApiClient client, string[] args, bool json)
    {
        var file = GetOption(args, "--file") ?? Positionals(args).FirstOrDefault();
        if (await ReadFileAsync(file, "sidehub-cli test import <scenarios.yaml>") is not { } yaml)
            return 1;

        var result = await client.ImportTestsAsync(yaml);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        var created = Items(result, "created");
        var updated = Items(result, "updated");
        Console.WriteLine($"Imported {file}: {created.Count} created, {updated.Count} updated.");
        foreach (var test in created) Console.WriteLine($"  + {Prop(test, "key")}");
        foreach (var test in updated) Console.WriteLine($"  ~ {Prop(test, "key")} (version {Prop(test, "version")})");
        return 0;
    }

    public static async Task<int> ExportAsync(SideHubApiClient client, string[] args, bool json)
    {
        string? repositoryId = null;
        if (GetOption(args, "--repository") is { } repository)
        {
            if (Guid.TryParse(repository, out _))
                repositoryId = repository;
            else
            {
                var repositories = await client.GetRepositoriesAsync();
                repositoryId = repositories.ValueKind == JsonValueKind.Array
                    ? repositories.EnumerateArray()
                        .Where(r => string.Equals(Prop(r, "name"), repository, StringComparison.OrdinalIgnoreCase))
                        .Select(r => Prop(r, "id")).FirstOrDefault()
                    : null;
                if (repositoryId is null)
                {
                    Console.Error.WriteLine($"Error: unknown repository '{repository}'. List them with: sidehub-cli repository list");
                    return 1;
                }
            }
        }

        var result = await client.ExportTestsAsync(repositoryId);
        var yaml = Prop(result, "yaml") ?? "";
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        if (GetOption(args, "--output") is { } output)
        {
            await File.WriteAllTextAsync(output, yaml);
            Console.WriteLine($"Exported the tests to {output}");
        }
        else
            Console.Write(yaml);
        return 0;
    }

    // ----- Playing tests -----

    public static async Task<int> RunAsync(SideHubApiClient client, string[] args, bool json)
    {
        var tests = GetOptions(args, "--test");
        var repository = GetOption(args, "--repository");
        var critical = args.Contains("--critical");
        var cli = GetOption(args, "--cli");
        if (new[] { tests.Count > 0, repository is not null, critical }.Count(given => given) > 1 || (cli is not null && !Clis.Contains(cli)))
        {
            Console.Error.WriteLine("Usage: sidehub-cli test run [--environment <name>] [--critical | --repository <name|id> | --test <key>...] [--fallbacks] [--cli claude|codex|agy|copilot] [--model <model>] [--agent <id>]");
            Console.Error.WriteLine("  Without a selection: every active test. --fallbacks: do not add the tests that set the values the selection uses.");
            return 1;
        }

        var selection = new Dictionary<string, object?>
        {
            ["kind"] = tests.Count > 0 ? "tests" : repository is not null ? "repository" : critical ? "critical" : "all",
            ["withProducers"] = !args.Contains("--fallbacks"),
        };
        if (tests.Count > 0) selection["tests"] = tests;
        if (repository is not null) selection[Guid.TryParse(repository, out _) ? "repositoryId" : "repository"] = repository;
        var body = new Dictionary<string, object?> { ["selection"] = selection };
        if (GetOption(args, "--environment") is { } environment) body["environment"] = environment;
        if (GetOption(args, "--agent") is { } agent) body["agentId"] = agent;
        if (cli is not null) body["provider"] = cli;
        if (GetOption(args, "--model") is { } model) body["model"] = model;

        var result = await client.LaunchTestsAsync(body);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }
        var execution = result.GetProperty("execution");
        var summary = execution.GetProperty("summary");
        Console.WriteLine($"Launched execution #{Prop(summary, "number")} ({Prop(summary, "id")}) on {Prop(summary, "environmentName")}: " +
            $"{Items(execution, "results").Count} test(s) played by {Prop(summary, "provider")}, {Items(execution, "skipped").Count} skipped.");
        foreach (var skipped in Items(execution, "skipped"))
            Console.WriteLine($"  skipped: {Prop(skipped, "key")} ({SkipReason(Prop(skipped, "reason"))})");
        Console.WriteLine($"Follow it: sidehub-cli test status {Prop(summary, "number")}");
        return 0;
    }

    public static async Task<int> StatusAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reference = Positionals(args).FirstOrDefault()?.TrimStart('#');
        var executions = await client.ListTestExecutionsAsync(reference is null ? 1 : 200);
        var list = executions.ValueKind == JsonValueKind.Array ? executions.EnumerateArray().ToList() : [];
        var id = reference is null
            ? list.Select(e => Prop(e, "id")).FirstOrDefault()
            : Guid.TryParse(reference, out _) ? reference
            : list.Where(e => Prop(e, "number") == reference).Select(e => Prop(e, "id")).FirstOrDefault();
        if (id is null)
        {
            Console.Error.WriteLine(reference is null
                ? "No test execution yet. Launch one with: sidehub-cli test run"
                : $"Error: no test execution #{reference}.");
            return reference is null ? 0 : 1;
        }

        var execution = await client.GetTestExecutionAsync(id);
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(execution));
            return 0;
        }

        var summary = execution.GetProperty("summary");
        var counts = summary.GetProperty("counts");
        var verdict = Prop(summary, "verdict") switch
        {
            "passed" => "PASSED: every test played passed, each with its evidence",
            "failed" => "FAILED: at least one failure confirmed by the re-check",
            "incomplete" => "INCOMPLETE: no confirmed failure, but tests are missing from the result",
            _ => Prop(summary, "status") ?? "-",
        };
        Console.WriteLine($"Execution #{Prop(summary, "number")}: {verdict}");
        Console.WriteLine($"Environment:  {Prop(summary, "environmentName")} ({Prop(summary, "environmentUrl")})");
        Console.WriteLine($"Played by:    {Prop(summary, "provider")} on {Prop(summary, "agentName") ?? Prop(summary, "agentId")}");
        Console.WriteLine($"Launched:     {PropDate(summary, "createdAt")}{(Prop(summary, "endedAt") is null ? ", still playing" : $", ended {PropDate(summary, "endedAt")}")}");
        Console.WriteLine($"Tests:        {Prop(counts, "played")} played: {Prop(counts, "passed")} passed, {Prop(counts, "failed")} failed, {Prop(counts, "flaky")} unstable, " +
            $"{Prop(counts, "inconclusive")} inconclusive, {Prop(counts, "blocked")} blocked, {Prop(counts, "pending")} pending; {Prop(counts, "skipped")} skipped");
        if (Prop(summary, "error") is { } error)
            Console.WriteLine($"Error:        {error}");

        Console.WriteLine();
        foreach (var result in Items(execution, "results"))
        {
            var steps = string.Join(" ", Items(result, "steps").Select(s => Prop(s, "status") switch
            {
                "passed" => "ok", "failed" => "KO", "inconclusive" => "??", "not-run" => "--", _ => "..",
            }));
            var status = Prop(result, "status") == "failed" && Bool(result, "confirmed") ? "failed (confirmed)" : Prop(result, "status");
            Console.WriteLine($"  {status,-18} {Truncate(Prop(result, "key") ?? "", 34),-34} [{steps}]{(Prop(result, "reason") is { } reason ? $"  {OneLine(reason, 110)}" : "")}");
            foreach (var step in Items(result, "steps").Where(s => Prop(s, "status") == "failed" && Prop(s, "observed") is not null))
                Console.WriteLine($"  {"",-18} observed: {OneLine(Prop(step, "observed")!, 140)}");
        }
        return 0;
    }

    public static async Task<int> ResultAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli test result <test id> --step <n> --status passed|failed|inconclusive [--then \"<n>=verified|failed|no-evidence: <what you observed>\"]* [--evidence \"...\"] [--observed \"...\"] [--console \"...\"]* [--http \"...\"]*";
        var key = Positionals(args, "--step", "--status", "--then", "--evidence", "--observed", "--console", "--http", "--capture").FirstOrDefault();
        var status = GetOption(args, "--status");
        if (key is null || !int.TryParse(GetOption(args, "--step"), out var step) || step < 1 || status is null || !StepStatuses.Contains(status))
        {
            Console.Error.WriteLine(usage);
            return 1;
        }
        if (ExecutionId() is not { } executionId)
            return 1;

        var then = new SortedDictionary<int, object>();
        foreach (var entry in GetOptions(args, "--then"))
        {
            // "<n>=<status>: <evidence>"
            var equals = entry.IndexOf('=');
            var colon = entry.IndexOf(':', Math.Max(0, equals));
            var thenStatus = equals < 0 ? null : (colon < 0 ? entry[(equals + 1)..] : entry[(equals + 1)..colon]).Trim().ToLowerInvariant();
            if (equals < 1 || !int.TryParse(entry[..equals].Trim(), out var index) || index < 1 || index > 50 || !ThenStatuses.Contains(thenStatus))
            {
                Console.Error.WriteLine($"Error: --then \"{entry}\" is not \"<n>=verified|failed|no-evidence: <what you observed>\" (n is the rank of the 'then' in the step).");
                return 1;
            }
            then[index] = new { status = thenStatus, evidence = colon < 0 ? null : entry[(colon + 1)..].Trim() };
        }
        // Every 'then' up to the last one given: one left out has no evidence.
        var thens = Enumerable.Range(1, then.Count == 0 ? 0 : then.Keys.Max())
            .Select(i => then.TryGetValue(i, out var given) ? given : new { status = "no-evidence", evidence = (string?)null })
            .ToList();

        var ack = await client.ReportTestResultAsync(executionId, new Dictionary<string, object?>
        {
            ["test"] = key,
            ["step"] = step,
            ["status"] = status,
            ["then"] = thens,
            ["evidence"] = GetOption(args, "--evidence"),
            ["observed"] = GetOption(args, "--observed"),
            ["console"] = GetOptions(args, "--console"),
            ["http"] = GetOptions(args, "--http"),
            ["capture"] = GetOption(args, "--capture"),
        });
        Console.WriteLine(json ? SideHubApiClient.Serialize(ack) : Prop(ack, "message") ?? "Recorded.");
        return 0;
    }

    /// <summary><c>test value set|get</c>: the values of the execution a step produces, and the ones a test uses.</summary>
    public static async Task<int> ValueAsync(SideHubApiClient client, string[] args, bool json)
    {
        var action = args.FirstOrDefault();
        var rest = Positionals(args.Skip(1).ToArray());
        var name = rest.FirstOrDefault();
        if (action is not ("set" or "get") || name is null || (action == "set" && rest.Count < 2))
        {
            Console.Error.WriteLine("Usage: sidehub-cli test value set <name> \"<value>\" [--secret] | get <name>");
            return 1;
        }
        if (ExecutionId() is not { } executionId)
            return 1;

        if (action == "set")
        {
            var value = rest[1];
            // A secret is masked in this terminal by SideHub itself, which tells the agent once it recorded the value.
            var set = await client.SetTestValueAsync(executionId, name, value, args.Contains("--secret"));
            Console.WriteLine(json
                ? SideHubApiClient.Serialize(set)
                : $"Set {Prop(set, "name")}{(Bool(set, "secret") ? " (secret: masked in this terminal, deleted when the execution ends)" : "")}.");
            return 0;
        }

        var read = await client.GetTestValueAsync(executionId, name);
        Console.WriteLine(json ? SideHubApiClient.Serialize(read) : Prop(read, "value") ?? "");
        return 0;
    }

    /// <summary><c>test results import</c>: the report of a Playwright run. Scripted tests are not played yet.</summary>
    public static Task<int> ResultsAsync(SideHubApiClient client, string[] args, bool json)
    {
        Console.Error.WriteLine(args.FirstOrDefault() == "import"
            ? "Error: scripted tests are not played yet: SideHub plays the tests written for an agent (mode: agent). Report each step with: sidehub-cli test result"
            : "Usage: sidehub-cli test results import <playwright-report.json>");
        return Task.FromResult(1);
    }

    // ----- Helpers -----

    private static string? ExecutionId()
    {
        var executionId = Environment.GetEnvironmentVariable(ExecutionVariable);
        if (!string.IsNullOrEmpty(executionId))
            return executionId;
        Console.Error.WriteLine($"Error: {ExecutionVariable} is not set.");
        Console.Error.WriteLine("This command can only run inside a run of a test execution (launch one with: sidehub-cli test run).");
        return null;
    }

    /// <summary>A test by its id, or by its key (looked up in the tests of the workspace, archived ones last).</summary>
    private static async Task<string> ResolveTestIdAsync(SideHubApiClient client, string reference)
    {
        if (Guid.TryParse(reference, out _))
            return reference;
        var tests = await client.ListTestsAsync(null, null, null);
        var id = tests.ValueKind == JsonValueKind.Array
            ? tests.EnumerateArray().Where(t => Prop(t, "key") == reference).Select(t => Prop(t, "id")).FirstOrDefault()
            : null;
        return id ?? throw new InvalidOperationException($"no test '{reference}'. List the tests with: sidehub-cli test list");
    }

    private static async Task<string?> ReadFileAsync(string? file, string usage)
    {
        if (string.IsNullOrEmpty(file))
        {
            Console.Error.WriteLine($"Usage: {usage}");
            return null;
        }
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"Error: file not found: {file}");
            return null;
        }
        return await File.ReadAllTextAsync(file);
    }

    private static string SkipReason(string? reason) => reason switch
    {
        "not-critical" => "the environment only plays critical tests",
        "no-cleanup" => "it writes data without a cleanup, and real data live in this environment",
        "script" => "scripted tests are not played yet",
        "not-active" => "it is a draft or archived",
        _ => reason ?? "skipped",
    };

    private static void PrintList(string label, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        Console.WriteLine($"{label}:");
        foreach (var line in list)
            Console.WriteLine($"  - {line}");
    }

    private static bool Confirm(string question)
    {
        Console.Error.Write($"{question} Type 'yes' to confirm: ");
        var input = Console.ReadLine();
        if (string.Equals(input?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            return true;
        Console.Error.WriteLine("Aborted.");
        return false;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static List<JsonElement> Items(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToList()
            : [];

    private static bool Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The arguments that are neither a flag nor the value of one of <paramref name="valueFlags"/>.</summary>
    private static List<string> Positionals(string[] args, params string[] valueFlags)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) result.Add(args[i]);
        }
        return result;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    /// <summary>Every value of a flag given several times.</summary>
    private static List<string> GetOptions(string[] args, string flag)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) values.Add(args[++i]);
        return values;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC")
            : "-";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private static string OneLine(string s, int max) => Truncate(s.ReplaceLineEndings(" ").Trim(), max);
}
