using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// What <c>sidehub-cli test play</c> decides without Playwright (SideHub doc 73): the commands that play the scripted
/// tests of a run, and what is kept of their reports.
/// </summary>
public sealed class CliScriptedTestsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidehub-scripted-tests-" + Guid.NewGuid().ToString("N"));

    public CliScriptedTestsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ScriptedTests.Test Script(string file, string? grep = null, params ScriptedTests.Use[] uses) =>
        new(Path.GetFileNameWithoutExtension(file), file, grep, uses, []);

    [Fact]
    public void Tests_are_grouped_by_the_nearest_playwright_configuration()
    {
        Write("web/playwright.config.ts");
        Write("web/e2e/admin/playwright.config.mjs");
        Write("web/e2e/login.spec.ts");
        Write("web/e2e/admin/users.spec.ts");
        Write("api/tests/health.spec.ts");

        var groups = ScriptedTests.Groups(
        [
            Script("web/e2e/login.spec.ts", "logs in"), Script("web/e2e/signup.spec.ts", "signs up"),
            Script("web/e2e/admin/users.spec.ts"), Script("api/tests/health.spec.ts"), Script("web/e2e/login.spec.ts", "logs out"),
        ], _root);

        Assert.Equal(3, groups.Count);
        Assert.Equal(Path.Combine(_root, "web"), groups[0].Folder);
        Assert.Equal(["e2e/login.spec.ts", "e2e/signup.spec.ts"], groups[0].Files);
        Assert.Equal("(logs in)|(signs up)|(logs out)", groups[0].Grep);
        Assert.Equal(Path.Combine(_root, "web", "e2e", "admin"), groups[1].Folder);
        Assert.Equal(["users.spec.ts"], groups[1].Files);
        // No configuration up to the folder of the run: Playwright runs there.
        Assert.Equal(_root, groups[2].Folder);
        Assert.Equal(["api/tests/health.spec.ts"], groups[2].Files);
    }

    [Fact]
    public void A_configuration_above_the_folder_of_the_run_is_not_looked_for()
    {
        Write("playwright.config.ts");
        var run = Path.Combine(_root, "repository");
        Directory.CreateDirectory(run);

        Assert.Equal(run, ScriptedTests.ProjectFolder(Path.Combine(run, "e2e", "smoke.spec.ts"), run));
        Assert.Equal(run, ScriptedTests.ProjectFolder(Path.Combine(_root, "elsewhere", "smoke.spec.ts"), run));
    }

    [Fact]
    public void A_command_takes_a_grep_only_when_each_of_its_tests_has_one()
    {
        Assert.Equal("(logs in)|(signs up)", ScriptedTests.JoinGreps(["logs in", "signs up", "logs in"]));
        Assert.Equal("(a|b)", ScriptedTests.JoinGreps(["a|b"]));
        Assert.Null(ScriptedTests.JoinGreps(["logs in", null]));
        Assert.Null(ScriptedTests.JoinGreps(["logs in", ""]));
        Assert.Null(ScriptedTests.JoinGreps([]));
    }

    [Fact]
    public void The_arguments_of_a_command_are_one_per_item()
    {
        var group = new ScriptedTests.Group(_root, ["e2e/login.spec.ts", "src/(app)/[id]/page.spec.ts"], "(logs in; rm -rf)|(it's \"ok\")");

        Assert.Equal(
            ["playwright", "test", "e2e/login.spec.ts", @"src/\(app\)/\[id\]/page.spec.ts", "--reporter=list,json", "--grep", "(logs in; rm -rf)|(it's \"ok\")", "--retries=0"],
            ScriptedTests.Arguments(group, recheck: true));
        Assert.Equal(
            ["playwright", "test", "e2e/login.spec.ts", "--reporter=list,json"],
            ScriptedTests.Arguments(new ScriptedTests.Group(_root, ["e2e/login.spec.ts"], null), recheck: false));
        Assert.Equal(
            "npx playwright test e2e/login.spec.ts --reporter=list,json --grep '(logs in)|(it'\\''s ok)'",
            ScriptedTests.Display(["playwright", "test", "e2e/login.spec.ts", "--reporter=list,json", "--grep", "(logs in)|(it's ok)"]));
    }

    [Fact]
    public void A_value_no_test_produced_is_read_in_the_secret_it_falls_back_on()
    {
        var environment = new Dictionary<string, string?>
        {
            ["QA_EMAIL"] = "qa@test.example.com",
            ["QA_TOKEN"] = "from-the-environment",
            ["SIDEHUB_TEST_VALUE_COMPTE_TOKEN"] = "already-set",
        };
        var tests = new[]
        {
            Script("a.spec.ts", null,
                new ScriptedTests.Use("compte.email", "SIDEHUB_TEST_VALUE_COMPTE_EMAIL", "fallback", "QA_EMAIL"),
                new ScriptedTests.Use("compte.token", "SIDEHUB_TEST_VALUE_COMPTE_TOKEN", "fallback", "QA_TOKEN"),
                new ScriptedTests.Use("compte.motDePasse", "SIDEHUB_TEST_VALUE_COMPTE_MOTDEPASSE", "fallback", "QA_PASSWORD"),
                new ScriptedTests.Use("compte.id", "SIDEHUB_TEST_VALUE_COMPTE_ID", "execution", "QA_EMAIL")),
        };

        var variables = ScriptedTests.Fallbacks(tests, name => environment.GetValueOrDefault(name));

        Assert.Equal(new Dictionary<string, string> { ["SIDEHUB_TEST_VALUE_COMPTE_EMAIL"] = "qa@test.example.com" }, variables);
    }

    [Fact]
    public void The_plan_is_read_as_the_backend_sends_it()
    {
        var plan = ScriptedTests.ReadPlan(JsonSerializer.Deserialize<JsonElement>("""
            {"executionId":"33333333-3333-3333-3333-333333333333","number":12,"phase":"recheck","recheck":true,"url":"https://app.test",
             "tests":[{"key":"smoke","title":"Smoke","file":"e2e/smoke.spec.ts","grep":null,"steps":2,
               "uses":[{"name":"compte.email","variable":"SIDEHUB_TEST_VALUE_COMPTE_EMAIL","source":"fallback","fallback":"QA_EMAIL"}],
               "produces":[{"name":"compte.motDePasse","secret":true},{"name":"compte.email","secret":false}]},
              {"key":"no-file","title":"x","file":"","grep":null,"steps":1,"uses":[],"produces":[]}]}
            """));

        Assert.Equal(12, plan.Number);
        Assert.True(plan.Recheck);
        var script = Assert.Single(plan.Scripts);
        Assert.Equal(("smoke", "e2e/smoke.spec.ts", null), (script.Key, script.File, script.Grep));
        Assert.Equal("QA_EMAIL", script.Uses!.Single().Fallback);
        Assert.True(plan.IsSecret("compte.motdepasse"));
        Assert.False(plan.IsSecret("compte.email"));
        Assert.False(plan.IsSecret("unknown"));
    }

    [Fact]
    public void Reports_are_merged_into_one_with_what_left_no_report()
    {
        var first = JsonNode.Parse("""{"config":{"version":"1.58.1"},"suites":[{"title":"a.spec.ts"}],"errors":[]}""")!.AsObject();
        var second = JsonNode.Parse("""{"config":{"version":"1.40.0"},"suites":[{"title":"b.spec.ts"},{"title":"c.spec.ts"}],"errors":[{"message":"SyntaxError"}]}""")!.AsObject();

        var merged = ScriptedTests.Merge([first, second], ["Playwright left no report for d.spec.ts (exit code 1)"]);

        Assert.Equal(["a.spec.ts", "b.spec.ts", "c.spec.ts"], merged["suites"]!.AsArray().Select(s => s!["title"]!.GetValue<string>()));
        Assert.Equal(["SyntaxError", "Playwright left no report for d.spec.ts (exit code 1)"], merged["errors"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()));
        Assert.Equal("1.58.1", merged["config"]!["version"]!.GetValue<string>());
        // The reports given are left as they were.
        Assert.Single(first["suites"]!.AsArray());

        var none = ScriptedTests.Merge([], ["npx could not start"]);
        Assert.Empty(none["suites"]!.AsArray());
        Assert.Equal("npx could not start", none["errors"]![0]!["message"]!.GetValue<string>());
    }

    private const string Report = """
        {"config":{"rootDir":"/repo/e2e"},
         "suites":[{"title":"smoke.spec.ts","file":"smoke.spec.ts","specs":[
            {"title":"logs in","file":"smoke.spec.ts","line":3,"tests":[{"projectName":"chromium","results":[
              {"status":"failed","duration":1200,"stdout":[{"text":"password: hunter2"}],"stderr":[{"text":"boom"}],
               "steps":[{"title":"Open the page","duration":10}],
               "attachments":[
                 {"name":"trace","contentType":"application/zip","path":"/out/trace.zip"},
                 {"name":"inline","contentType":"image/png","body":"aW5saW5l"},
                 {"name":"screenshot","contentType":"image/png","path":"/out/failed-1.png","body":"c2hvdA=="},
                 {"name":"screenshot","contentType":"image/png","path":"/out/failed-2.png"}]},
              {"status":"passed","stdout":[],"stderr":[],"attachments":[{"name":"screenshot","contentType":"image/png","path":"/out/passed.png"}]}]}]}],
            "suites":[{"title":"nested","specs":[
              {"title":"fails twice","tests":[{"results":[
                {"status":"failed","retry":0,"attachments":[{"name":"screenshot","contentType":"image/png","path":"/out/first-try.png"}]},
                {"status":"failed","retry":1,"attachments":[{"name":"error-context","contentType":"text/markdown","path":"/out/error-context.md"},{"name":"screenshot","contentType":"image/png","path":"/out/retry-1.png"}]}]}]},
              {"title":"times out","tests":[{"results":[{"status":"timedOut","attachments":[{"name":"screenshot","contentType":"image/jpeg","path":"/out/timeout.jpg"}]}]}]},
              {"title":"too big","tests":[{"results":[{"status":"failed","attachments":[{"name":"screenshot","contentType":"image/png","path":"/out/huge.png"},{"name":"other","contentType":"image/png","path":"/out/small.png"}]}]}]},
              {"title":"skipped","tests":[{"results":[{"status":"skipped","attachments":[{"name":"screenshot","contentType":"image/png","path":"/out/skipped.png"}]}]}]}]}]}],
         "errors":[{"message":"x","stdout":"kept out"}]}
        """;

    [Fact]
    public void A_report_is_sent_without_the_output_of_the_tests_nor_the_attachments_written_in_it()
    {
        var report = JsonNode.Parse(Report)!.AsObject();

        ScriptedTests.Slim(report);

        var json = report.ToJsonString();
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("stdout", json);
        Assert.DoesNotContain("stderr", json);
        Assert.DoesNotContain("\"body\"", json);
        var result = report["suites"]![0]!["specs"]![0]!["tests"]![0]!["results"]![0]!;
        Assert.Equal("failed", result["status"]!.GetValue<string>());
        Assert.Equal(1200, result["duration"]!.GetValue<int>());
        Assert.Equal("Open the page", result["steps"]![0]!["title"]!.GetValue<string>());
        Assert.Equal(
            ["/out/trace.zip", null, "/out/failed-1.png", "/out/failed-2.png"],
            result["attachments"]!.AsArray().Select(a => a!["path"]?.GetValue<string>()));
        Assert.Equal("/repo/e2e", report["config"]!["rootDir"]!.GetValue<string>());
    }

    [Fact]
    public void The_image_of_a_failure_is_the_first_one_playwright_attached_as_a_file()
    {
        var report = JsonNode.Parse(Report)!.AsObject();

        var images = ScriptedTests.FailureImages(report, path => path != "/out/huge.png");

        // The last result of a test counts: not the image of an earlier try, of a test that passed in the end or was
        // skipped, nor a later image when the first one cannot be sent.
        Assert.Equal(["/out/retry-1.png", "/out/timeout.jpg"], images);
        Assert.Empty(ScriptedTests.FailureImages(new JsonObject(), _ => true));
    }

    [Fact]
    public void A_real_report_of_playwright_gives_the_image_of_the_last_try_of_its_failure()
    {
        // Playwright 1.58.1, --reporter=list,json: three files, one test failing twice (one retry), each try with its screenshot.
        var report = ScriptedTests.ReadReport(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playwright", "report-1.58.json"))!;

        var image = Assert.Single(ScriptedTests.FailureImages(report, _ => true));
        ScriptedTests.Slim(report);

        Assert.Equal("/repo/test-results/broken-the-cockpit-opens-w-5111f-igning-in-fails-on-purpose--chromium-retry1/test-failed-1.png", image);
        Assert.DoesNotContain("\"stdout\"", report.ToJsonString());
        Assert.DoesNotContain("\"stderr\"", report.ToJsonString());
        Assert.Equal(["access.spec.ts", "broken.spec.ts", "values.spec.ts"], report["suites"]!.AsArray().Select(s => s!["file"]!.GetValue<string>()));
        Assert.Contains(image, report.ToJsonString());
    }

    [Fact]
    public void An_image_is_sent_when_it_exists_and_is_not_too_large()
    {
        var small = Write("out/smoke-logs-in-chromium/test-failed-1.png", "png");
        var large = Write("out/large.png");
        using (var stream = File.OpenWrite(large))
            stream.SetLength(ScriptedTests.MaxImageBytes + 1);

        Assert.True(ScriptedTests.IsUsableImage(small));
        Assert.False(ScriptedTests.IsUsableImage(large));
        Assert.False(ScriptedTests.IsUsableImage(Path.Combine(_root, "out", "missing.png")));
        Assert.Equal("smoke-logs-in-chromium test-failed-1.png", ScriptedTests.CaptureTitle(small));
    }

    [Fact]
    public void The_values_a_script_wrote_are_read_by_name()
    {
        var values = ScriptedTests.ReadValues(
            Write("values.json", """{"compte.email":"qa+12@test.example.com","compte.rang":3,"compte.actif":true,"compte.vide":null,"compte.objet":{"a":1}}"""),
            out var problem);

        Assert.Null(problem);
        Assert.Equal(
            new Dictionary<string, string> { ["compte.email"] = "qa+12@test.example.com", ["compte.rang"] = "3", ["compte.actif"] = "true" },
            values);
    }

    [Fact]
    public void A_values_file_that_is_missing_or_not_an_object_gives_no_value()
    {
        Assert.Empty(ScriptedTests.ReadValues(Path.Combine(_root, "missing.json"), out var missing));
        Assert.Null(missing);
        Assert.Empty(ScriptedTests.ReadValues(Write("list.json", """["compte.email"]"""), out var list));
        Assert.NotNull(list);
        Assert.Empty(ScriptedTests.ReadValues(Write("broken.json", "{\"compte.email\": "), out var broken));
        Assert.NotNull(broken);
    }

    [Fact]
    public void A_report_that_is_missing_or_not_one_is_not_read()
    {
        Assert.Null(ScriptedTests.ReadReport(Path.Combine(_root, "missing.json")));
        Assert.Null(ScriptedTests.ReadReport(Write("empty.json")));
        Assert.Null(ScriptedTests.ReadReport(Write("list.json", "[]")));
        Assert.NotNull(ScriptedTests.ReadReport(Write("report.json", """{"suites":[]}""")));
    }
}
