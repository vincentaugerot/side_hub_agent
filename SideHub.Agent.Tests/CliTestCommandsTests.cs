using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// <c>sidehub-cli test</c> (SideHub doc 73): the catalog of tests sent as files, an execution launched and followed, and
/// what a run of an execution reports (each step, each value; for a run of scripts, the report of Playwright).
/// </summary>
public sealed class CliTestCommandsTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private const string TestId = "22222222-2222-2222-2222-222222222222";
    private const string ExecutionId = "33333333-3333-3333-3333-333333333333";
    private const string TestFile = "title: Log in\nsteps:\n  - when: I log in\n    then: [the cockpit is shown]\n";

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sidehub-test-commands-" + Guid.NewGuid().ToString("N"));
    private readonly string? _execution = Environment.GetEnvironmentVariable(TestCommands.ExecutionVariable);

    public CliTestCommandsTests()
    {
        _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, _execution);
        _client.Dispose();
        _api.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private string File(string content, string name = "test.yaml")
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private const string Catalog = """
        [{"id":"22222222-2222-2222-2222-222222222222","key":"login","title":"Log in","status":"active","priority":"critical","mode":"agent",
          "feature":"Account","steps":[{"title":"s","when":"w","then":["t"],"produces":[]}],"history":[],
          "repositories":[{"id":"r-1","name":"side_hub"}]},
         {"id":"44444444-4444-4444-4444-444444444444","key":"agent-update","title":"Update the agent","status":"active","priority":"normal","mode":"agent",
          "feature":"Agent","steps":[],"history":[],"repositories":[{"id":"r-2","name":"side_hub_agent"}]}]
        """;

    [Fact]
    public async Task List_sends_the_filters_and_narrows_a_repository_given_by_its_name()
    {
        _api.Respond("/tests", Catalog);

        Assert.Equal(0, await TestCommands.ListAsync(_client, ["--repository", "side_hub_agent", "--status", "active", "--feature", "Agent"], json: true));

        Assert.Equal([$"/api/workspaces/{WorkspaceId}/tests?feature=Agent&status=active"], _api.Requests);
        Assert.Equal(1, await TestCommands.ListAsync(_client, ["--status", "broken"], json: false));
    }

    [Fact]
    public async Task Create_from_a_terminal_its_user_opened_makes_an_active_test()
    {
        _api.Respond("/agent/me", """{"token":"terminal","actingUser":{"email":"ana@test.com","role":"owner"}}""");
        _api.Respond("/tests", """{"id":"t-1","key":"log-in","status":"active"}""");

        Assert.Equal(0, await TestCommands.CreateAsync(_client, ["--file", File(TestFile)], json: false));

        Assert.Equal(["GET /api/agent/me", $"POST /api/workspaces/{WorkspaceId}/tests"], _api.Methods);
        Assert.Equal(TestFile, _api.Calls[1].Json.GetProperty("yaml").GetString());
    }

    [Theory]
    [InlineData("""{"token":"run","runId":"r-1"}""")]
    [InlineData("""{"token":"terminal"}""")]
    public async Task Create_from_a_run_or_a_queued_task_only_proposes(string identity)
    {
        _api.Respond("/agent/me", identity);
        _api.Respond("/tests/proposals", """{"id":"t-1","key":"log-in","status":"draft"}""");

        Assert.Equal(0, await TestCommands.CreateAsync(_client, ["--file", File(TestFile)], json: false));

        Assert.Equal($"POST /api/workspaces/{WorkspaceId}/tests/proposals", _api.Methods[1]);
    }

    [Fact]
    public async Task Create_needs_a_file_that_exists()
    {
        Assert.Equal(1, await TestCommands.CreateAsync(_client, [], json: false));
        Assert.Equal(1, await TestCommands.CreateAsync(_client, ["--file", Path.Combine(_folder, "missing.yaml")], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task What_the_backend_refuses_is_told_as_it_says_it()
    {
        _api.Respond("/agent/me", """{"token":"terminal","actingUser":{"email":"ana@test.com","role":"owner"}}""");
        _api.Respond("/tests", """{"error":"The value 'page.titre' is used by 'memory' but no test produces it."}""", HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => TestCommands.CreateAsync(_client, ["--file", File(TestFile)], json: false));

        Assert.Equal("The value 'page.titre' is used by 'memory' but no test produces it.", error.Message);
    }

    [Fact]
    public async Task Update_and_archive_find_a_test_by_its_key()
    {
        _api.Respond("/tests", Catalog);
        _api.Respond($"/tests/{TestId}", """{"key":"login","version":2}""");
        _api.Respond($"/tests/{TestId}/archive", """{"key":"login","status":"archived"}""");

        Assert.Equal(0, await TestCommands.UpdateAsync(_client, ["login", "--file", File(TestFile)], json: false));
        Assert.Equal(0, await TestCommands.ArchiveAsync(_client, ["login", "--yes"], json: false));

        Assert.Equal(
        [
            $"GET /api/workspaces/{WorkspaceId}/tests", $"PUT /api/tests/{TestId}",
            $"GET /api/workspaces/{WorkspaceId}/tests", $"POST /api/tests/{TestId}/archive",
        ], _api.Methods);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => TestCommands.GetAsync(_client, ["nope"], json: true));
        Assert.Contains("sidehub-cli test list", unknown.Message);
    }

    [Fact]
    public async Task Import_sends_the_catalog_and_export_writes_the_file()
    {
        _api.Respond("/tests/import", """{"created":[{"key":"login"}],"updated":[{"key":"signup","version":3}]}""");
        _api.Respond("/repositories", """[{"id":"r-1","name":"side_hub"}]""");
        _api.Respond("/tests/export", """{"yaml":"format: sidehub.qa/v1\nscenarios: []\n"}""");
        var output = Path.Combine(_folder, "out.yaml");

        Assert.Equal(0, await TestCommands.ImportAsync(_client, [File("scenarios: []", "scenarios.yaml")], json: false));
        Assert.Equal(0, await TestCommands.ExportAsync(_client, ["--repository", "SIDE_HUB", "--output", output], json: false));
        Assert.Equal(1, await TestCommands.ExportAsync(_client, ["--repository", "elsewhere"], json: false));

        Assert.Equal("scenarios: []", _api.Calls[0].Json.GetProperty("yaml").GetString());
        Assert.Contains($"/api/workspaces/{WorkspaceId}/tests/export?repositoryId=r-1", _api.Requests);
        Assert.Equal("format: sidehub.qa/v1\nscenarios: []\n", System.IO.File.ReadAllText(output));
    }

    [Fact]
    public async Task Run_sends_the_selection_and_what_plays_it()
    {
        _api.Respond("/test-executions", """{"execution":{"summary":{"id":"e-1","number":7,"environmentName":"Staging","provider":"codex"},"results":[{}],"skipped":[{"key":"redirect","reason":"script"}]}}""");

        Assert.Equal(0, await TestCommands.RunAsync(_client,
            ["--environment", "Staging", "--test", "login", "--test", "signup", "--fallbacks", "--cli", "codex", "--model", "gpt-5"], json: false));

        var body = _api.Calls.Single().Json;
        Assert.Equal("Staging", body.GetProperty("environment").GetString());
        Assert.Equal("codex", body.GetProperty("provider").GetString());
        Assert.Equal("gpt-5", body.GetProperty("model").GetString());
        var selection = body.GetProperty("selection");
        Assert.Equal("tests", selection.GetProperty("kind").GetString());
        Assert.Equal(["login", "signup"], selection.GetProperty("tests").EnumerateArray().Select(t => t.GetString()));
        Assert.False(selection.GetProperty("withProducers").GetBoolean());
    }

    [Theory]
    [InlineData(new string[0], "all", null)]
    [InlineData(new[] { "--critical" }, "critical", null)]
    [InlineData(new[] { "--repository", "side_hub" }, "repository", "side_hub")]
    public async Task Run_without_named_tests_takes_all_the_critical_ones_or_a_repository(string[] args, string kind, string? repository)
    {
        _api.Respond("/test-executions", """{"execution":{"summary":{"id":"e-1","number":1},"results":[],"skipped":[]}}""");

        Assert.Equal(0, await TestCommands.RunAsync(_client, args, json: false));

        var selection = _api.Calls.Single().Json.GetProperty("selection");
        Assert.Equal(kind, selection.GetProperty("kind").GetString());
        Assert.True(selection.GetProperty("withProducers").GetBoolean());
        Assert.Equal(repository, selection.TryGetProperty("repository", out var name) ? name.GetString() : null);
    }

    [Fact]
    public async Task Run_takes_one_kind_of_selection_and_a_known_cli()
    {
        Assert.Equal(1, await TestCommands.RunAsync(_client, ["--critical", "--test", "login"], json: false));
        Assert.Equal(1, await TestCommands.RunAsync(_client, ["--cli", "gemini"], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task A_launch_that_cannot_start_says_why()
    {
        _api.Respond("/test-executions", """{"error":"Agent 'vps' is offline.","code":"agent-offline"}""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => TestCommands.RunAsync(_client, [], json: false));

        Assert.Equal("Agent 'vps' is offline.", error.Message);
    }

    [Fact]
    public async Task Status_reads_the_last_execution_or_one_by_its_number()
    {
        _api.Respond("/test-executions", $$"""[{"id":"{{ExecutionId}}","number":7},{"id":"e-6","number":6}]""");
        _api.Respond($"/test-executions/{ExecutionId}", """{"summary":{"number":7,"verdict":"failed","counts":{"played":1,"failed":1}},"results":[{"key":"login","status":"failed","confirmed":true,"steps":[{"status":"failed","observed":"500"}]}]}""");
        _api.Respond("/test-executions/e-6", """{"summary":{"number":6,"status":"running","counts":{}},"results":[]}""");

        Assert.Equal(0, await TestCommands.StatusAsync(_client, [], json: false));
        Assert.Equal(0, await TestCommands.StatusAsync(_client, ["#6"], json: false));
        Assert.Equal(1, await TestCommands.StatusAsync(_client, ["99"], json: false));

        Assert.Contains($"/api/test-executions/{ExecutionId}", _api.Requests);
        Assert.Contains("/api/test-executions/e-6", _api.Requests);
    }

    [Fact]
    public async Task Result_reports_a_step_with_each_then_in_its_rank()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/results", """{"testKey":"login","status":"pending","message":"Step 1 of 2 recorded."}""");

        Assert.Equal(0, await TestCommands.ResultAsync(_client,
        [
            "login", "--step", "1", "--status", "failed",
            "--then", "1=verified: the URL is /cockpit: no redirect", "--then", "3=failed: the agent is offline",
            "--observed", "The agent card says offline", "--console", "TypeError: x", "--console", "ReferenceError: y", "--http", "GET /api/agents 500",
        ], json: false));

        Assert.Equal([$"POST /api/test-executions/{ExecutionId}/results"], _api.Methods);
        var body = _api.Calls.Single().Json;
        Assert.Equal("login", body.GetProperty("test").GetString());
        Assert.Equal(1, body.GetProperty("step").GetInt32());
        Assert.Equal("failed", body.GetProperty("status").GetString());
        var then = body.GetProperty("then").EnumerateArray().ToList();
        Assert.Equal(["verified", "no-evidence", "failed"], then.Select(t => t.GetProperty("status").GetString()));
        Assert.Equal("the URL is /cockpit: no redirect", then[0].GetProperty("evidence").GetString());
        Assert.Equal(JsonValueKind.Null, then[1].GetProperty("evidence").ValueKind);
        Assert.Equal(["TypeError: x", "ReferenceError: y"], body.GetProperty("console").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("The agent card says offline", body.GetProperty("observed").GetString());
    }

    [Theory]
    [InlineData("login", "--step", "0", "--status", "passed")]
    [InlineData("login", "--step", "1", "--status", "ok")]
    [InlineData("login", "--step", "1", "--status", "passed", "--then", "verified: seen")]
    [InlineData("login", "--step", "1", "--status", "passed", "--then", "1=seen")]
    [InlineData("--step", "1", "--status", "passed")]
    public async Task Result_refuses_what_it_cannot_read(params string[] args)
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);

        Assert.Equal(1, await TestCommands.ResultAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Result_and_value_only_work_inside_a_run_of_an_execution()
    {
        Assert.Equal(1, await TestCommands.ResultAsync(_client, ["login", "--step", "1", "--status", "passed"], json: false));
        Assert.Equal(1, await TestCommands.ValueAsync(_client, ["get", "compte.email"], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Value_sets_and_reads_by_name()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/values/compte.motDePasse", """{"name":"compte.motDePasse","secret":true,"value":"s3cret-pw"}""");

        Assert.Equal(0, await TestCommands.ValueAsync(_client, ["set", "compte.motDePasse", "s3cret-pw", "--secret"], json: false));
        Assert.Equal(0, await TestCommands.ValueAsync(_client, ["get", "compte.motDePasse"], json: true));
        Assert.Equal(1, await TestCommands.ValueAsync(_client, ["set", "compte.motDePasse"], json: false));
        Assert.Equal(1, await TestCommands.ValueAsync(_client, ["drop", "compte.motDePasse"], json: false));

        Assert.Equal(
        [
            $"PUT /api/test-executions/{ExecutionId}/values/compte.motDePasse",
            $"GET /api/test-executions/{ExecutionId}/values/compte.motDePasse",
        ], _api.Methods);
        Assert.Equal("s3cret-pw", _api.Calls[0].Json.GetProperty("value").GetString());
        Assert.True(_api.Calls[0].Json.GetProperty("secret").GetBoolean());
    }

    // ----- Scripted tests -----

    private const string ScriptPlan = """
        {"executionId":"33333333-3333-3333-3333-333333333333","number":12,"phase":"suite","recheck":false,"url":"https://app.test",
         "tests":[
           {"key":"smoke","title":"Smoke","file":"e2e/smoke.spec.ts","grep":"logs in","steps":2,
            "uses":[{"name":"compte.email","variable":"SIDEHUB_TEST_VALUE_COMPTE_EMAIL_CLI_TEST","source":"fallback","fallback":"SIDEHUB_CLI_TEST_QA_EMAIL"}],
            "produces":[{"name":"compte.motDePasse","secret":true},{"name":"compte.id","secret":false}]},
           {"key":"signup","title":"Sign up","file":"e2e/signup.spec.ts","grep":"signs up","steps":1,"uses":[],"produces":[]}]}
        """;

    private const string ImportAck = """
        {"tests":[{"key":"smoke","status":"failed","confirmed":false,"stoppedAt":2,"reason":"expected /cockpit"},
                  {"key":"signup","status":"passed","confirmed":false,"stoppedAt":null,"reason":null}]}
        """;

    /// <summary>A Playwright report with one failure, which shows <paramref name="image"/>.</summary>
    private static string PlaywrightReport(string image) => new JsonObject
    {
        ["suites"] = new JsonArray(new JsonObject
        {
            ["title"] = "smoke.spec.ts",
            ["specs"] = new JsonArray(new JsonObject
            {
                ["title"] = "logs in",
                ["tests"] = new JsonArray(new JsonObject
                {
                    ["results"] = new JsonArray(new JsonObject
                    {
                        ["status"] = "failed",
                        ["stdout"] = new JsonArray(new JsonObject { ["text"] = "typed s3cret-pw" }),
                        ["attachments"] = new JsonArray(new JsonObject { ["name"] = "screenshot", ["contentType"] = "image/png", ["path"] = image, ["body"] = "c2hvdA==" }),
                    }),
                }),
            }),
        }),
        ["errors"] = new JsonArray(),
    }.ToJsonString();

    [Fact]
    public async Task Play_runs_playwright_then_sends_the_values_the_capture_and_the_report()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        Environment.SetEnvironmentVariable("SIDEHUB_CLI_TEST_QA_EMAIL", "qa@test.example.com");
        var image = File("png", "test-failed-1.png");
        _api.Respond("/script-plan", ScriptPlan);
        _api.Respond("/values/compte.motDePasse", """{"name":"compte.motDePasse","secret":true}""");
        _api.Respond("/values/compte.id", """{"name":"compte.id","secret":false}""");
        _api.Respond("/values/compte.inconnu", """{"error":"'compte.inconnu' is not a value the tests of this run produce."}""", HttpStatusCode.BadRequest);
        _api.Respond("/drive", """{"items":[{"id":"f-archived","type":"folder","status":"archived","title":"Tests","children":[]},{"id":"p-1","type":"page","status":"active","title":"Tests"}]}""", method: "GET");
        _api.Respond("/drive", """{"id":"f-new"}""", method: "POST");
        _api.Respond("/drive/upload", """{"id":"44444444-4444-4444-4444-444444444444","fileName":"test-failed-1.png"}""");
        _api.Respond("/results/import", ImportAck);
        ProcessStartInfo? started = null;

        var exit = await TestCommands.PlayAsync(_client, [], json: false, startInfo =>
        {
            started = startInfo;
            System.IO.File.WriteAllText(startInfo.Environment[ScriptedTests.ReportFileVariable]!, PlaywrightReport(image));
            System.IO.File.WriteAllText(startInfo.Environment[ScriptedTests.ValuesFileVariable]!,
                """{"compte.motDePasse":"s3cret-pw","compte.id":"42","compte.inconnu":"x"}""");
            // Playwright exits 1 when a test failed: the report says what happened.
            return 1;
        });
        Environment.SetEnvironmentVariable("SIDEHUB_CLI_TEST_QA_EMAIL", null);

        Assert.Equal(0, exit);
        Assert.NotNull(started);
        Assert.False(started.UseShellExecute);
        Assert.Equal(Directory.GetCurrentDirectory(), started.WorkingDirectory);
        Assert.Equal(
            ["playwright", "test", "e2e/smoke.spec.ts", "e2e/signup.spec.ts", "--reporter=list,json", "--grep", "(logs in)|(signs up)"],
            started.ArgumentList.SkipWhile(a => a != "playwright"));
        Assert.Equal("qa@test.example.com", started.Environment["SIDEHUB_TEST_VALUE_COMPTE_EMAIL_CLI_TEST"]);
        Assert.Equal(started.Environment[ScriptedTests.ReportFileVariable], started.Environment[ScriptedTests.LegacyReportFileVariable]);
        Assert.Equal("false", started.Environment["npm_config_yes"]);
        // What Playwright and the scripts wrote is gone once sent.
        Assert.False(Directory.Exists(Path.GetDirectoryName(started.Environment[ScriptedTests.ValuesFileVariable])));

        Assert.Equal(
        [
            $"GET /api/test-executions/{ExecutionId}/script-plan",
            $"PUT /api/test-executions/{ExecutionId}/values/compte.motDePasse",
            $"PUT /api/test-executions/{ExecutionId}/values/compte.id",
            $"PUT /api/test-executions/{ExecutionId}/values/compte.inconnu",
            $"GET /api/workspaces/{WorkspaceId}/drive",
            $"POST /api/workspaces/{WorkspaceId}/drive",
            $"POST /api/workspaces/{WorkspaceId}/drive",
            $"POST /api/workspaces/{WorkspaceId}/drive/upload",
            $"POST /api/test-executions/{ExecutionId}/results/import",
        ], _api.Methods);
        Assert.True(_api.Calls[1].Json.GetProperty("secret").GetBoolean());
        Assert.False(_api.Calls[2].Json.GetProperty("secret").GetBoolean());
        Assert.Equal(("Tests", "folder"), (_api.Calls[5].Json.GetProperty("title").GetString(), _api.Calls[5].Json.GetProperty("type").GetString()));
        Assert.False(_api.Calls[5].Json.TryGetProperty("parentId", out _));
        Assert.Equal(("Execution #12", "f-new"), (_api.Calls[6].Json.GetProperty("title").GetString(), _api.Calls[6].Json.GetProperty("parentId").GetString()));
        Assert.Contains("parentId=f-new", _api.Calls[7].PathAndQuery);

        var import = _api.Calls[8].Json;
        Assert.Equal("drive:44444444-4444-4444-4444-444444444444", import.GetProperty("captures").GetProperty(image).GetString());
        var result = import.GetProperty("report").GetProperty("suites")[0].GetProperty("specs")[0].GetProperty("tests")[0].GetProperty("results")[0];
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.False(result.TryGetProperty("stdout", out _));
        Assert.Equal(image, result.GetProperty("attachments")[0].GetProperty("path").GetString());
        Assert.False(result.GetProperty("attachments")[0].TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Play_tells_the_backend_when_playwright_left_no_report()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/script-plan", ScriptPlan.Replace("\"recheck\":false", "\"recheck\":true").Replace("\"grep\":\"signs up\"", "\"grep\":null"));
        _api.Respond("/results/import", """{"tests":[{"key":"smoke","status":"inconclusive","confirmed":false,"stoppedAt":1,"reason":"Playwright played no test"}]}""");
        var commands = new List<List<string>>();

        Assert.Equal(0, await TestCommands.PlayAsync(_client, [], json: false, startInfo =>
        {
            commands.Add(startInfo.ArgumentList.SkipWhile(a => a != "playwright").ToList());
            return 1;
        }));
        Assert.Equal(0, await TestCommands.PlayAsync(_client, [], json: true, _ => throw new System.ComponentModel.Win32Exception(2)));

        // One test of the command has no grep: Playwright plays the files, without retry when it plays them again.
        Assert.Equal(["playwright", "test", "e2e/smoke.spec.ts", "e2e/signup.spec.ts", "--reporter=list,json", "--retries=0"], commands.Single());
        var imports = _api.Calls.Where(c => c.PathAndQuery.EndsWith("/results/import")).Select(c => c.Json.GetProperty("report")).ToList();
        Assert.Equal(2, imports.Count);
        Assert.Empty(imports[0].GetProperty("suites").EnumerateArray());
        Assert.Contains("Playwright left no report for e2e/smoke.spec.ts, e2e/signup.spec.ts (exit code 1)", imports[0].GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.StartsWith("npx could not start", imports[1].GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Play_with_no_scripted_test_runs_nothing()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/script-plan", """{"executionId":"33333333-3333-3333-3333-333333333333","number":12,"phase":"suite","recheck":false,"url":"https://app.test","tests":[]}""");

        Assert.Equal(0, await TestCommands.PlayAsync(_client, [], json: false, _ => throw new InvalidOperationException("nothing to start")));

        Assert.Equal([$"GET /api/test-executions/{ExecutionId}/script-plan"], _api.Methods);
    }

    [Fact]
    public async Task Play_and_results_import_only_work_inside_a_run_of_an_execution()
    {
        Assert.Equal(1, await TestCommands.PlayAsync(_client, [], json: false, _ => 0));
        Assert.Equal(1, await TestCommands.ResultsAsync(_client, ["import", File("""{"suites":[]}""", "report.json")], json: false));
        Assert.Empty(_api.Calls);

        // A run of tests written for an agent has no script to play: the backend says so.
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/script-plan", """{"error":"This run plays tests written for an agent, not scripts: report each step with sidehub-cli test result."}""", HttpStatusCode.BadRequest);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => TestCommands.PlayAsync(_client, [], json: false, _ => 0));
        Assert.StartsWith("This run plays tests written for an agent", refused.Message);
    }

    [Fact]
    public async Task Results_import_sends_a_report_somebody_produced_with_its_values()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/script-plan", ScriptPlan);
        _api.Respond("/values/compte.motDePasse", """{"name":"compte.motDePasse","secret":true}""");
        _api.Respond("/results/import", ImportAck);
        var report = File(PlaywrightReport(Path.Combine(_folder, "missing.png")), "report.json");
        var values = File("""{"compte.motDePasse":"s3cret-pw"}""", "values.json");

        Assert.Equal(0, await TestCommands.ResultsAsync(_client, ["import", report, "--values", values], json: true));

        // The image of the failure is not on this machine: the report is sent without it.
        Assert.Equal(
        [
            $"GET /api/test-executions/{ExecutionId}/script-plan",
            $"PUT /api/test-executions/{ExecutionId}/values/compte.motDePasse",
            $"POST /api/test-executions/{ExecutionId}/results/import",
        ], _api.Methods);
        Assert.True(_api.Calls[1].Json.GetProperty("secret").GetBoolean());
        Assert.Empty(_api.Calls[2].Json.GetProperty("captures").EnumerateObject());
        Assert.Single(_api.Calls[2].Json.GetProperty("report").GetProperty("suites").EnumerateArray());
    }

    [Fact]
    public async Task Results_import_needs_a_report_it_can_read()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);

        Assert.Equal(1, await TestCommands.ResultsAsync(_client, [], json: false));
        Assert.Equal(1, await TestCommands.ResultsAsync(_client, ["import"], json: false));
        Assert.Equal(1, await TestCommands.ResultsAsync(_client, ["import", Path.Combine(_folder, "missing.json")], json: false));
        Assert.Equal(1, await TestCommands.ResultsAsync(_client, ["import", File("[]", "list.json")], json: false));
        Assert.Equal(1, await TestCommands.ResultsAsync(_client, ["import", File("{}", "report.json"), "--values", Path.Combine(_folder, "missing.json")], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task A_report_that_cannot_be_sent_fails_the_command()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/script-plan", ScriptPlan);
        _api.Respond("/results/import", """{"error":"This execution is over."}""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => TestCommands.ResultsAsync(_client, ["import", File("""{"suites":[]}""", "report.json")], json: false));

        Assert.Equal("This execution is over.", error.Message);
    }
}
