using SideHub.Agent.Testing;

namespace SideHub.Agent.Tests;

/// <summary>The source of a scripted test, read in the folder of the agent for the page of the test (SideHub doc 73).</summary>
public class TestScriptSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-test-script-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_script_of_the_folder_is_read_as_it_is()
    {
        const string spec = "import { test } from '@playwright/test';\n\ntest('logs in', async ({ page }) => {\n  await test.step('Je me connecte', async () => {});\n});\n";
        Write("qa/e2e/login.spec.ts", "﻿" + spec);

        Assert.Equal(new TestScriptText(spec, null), TestScriptSource.Read(_root, "qa/e2e/login.spec.ts"));
        Assert.Equal(spec, TestScriptSource.Read(_root, " qa\\e2e\\login.spec.ts ").Content);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/secrets.json")]
    [InlineData("qa/e2e/login.spec.ts.bak")]
    [InlineData("qa/e2e")]
    [InlineData("")]
    public void Only_a_playwright_file_is_read(string file)
    {
        Write(".env", "TOKEN=secret");
        Write("config/secrets.json", "{}");
        Write("qa/e2e/login.spec.ts.bak", "old");

        Assert.Equal(new TestScriptText(null, TestScriptSource.NotAScript), TestScriptSource.Read(_root, file));
        Assert.Equal(TestScriptSource.NotAScript, TestScriptSource.Read(_root, null).Error);
    }

    [Fact]
    public void Nothing_outside_the_folder_is_read()
    {
        var outside = Directory.CreateTempSubdirectory("sidehub-test-script-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.ts"), "export const token = 'secret';");
            Write("e2e/login.spec.ts", "ok");

            Assert.Null(TestScriptSource.Read(_root, Path.Combine(outside, "secret.ts")).Content);
            Assert.Null(TestScriptSource.Read(_root, $"../{Path.GetFileName(outside)}/secret.ts").Content);
            Assert.Null(TestScriptSource.Read(_root, "e2e/../../" + Path.GetFileName(outside) + "/secret.ts").Content);
            if (!OperatingSystem.IsWindows())
            {
                // A link committed in the repository does not lead out of it
                File.CreateSymbolicLink(Path.Combine(_root, "e2e", "linked.spec.ts"), Path.Combine(outside, "secret.ts"));
                Directory.CreateSymbolicLink(Path.Combine(_root, "away"), outside);
                Assert.Equal(new TestScriptText(null, TestScriptSource.Unreadable), TestScriptSource.Read(_root, "e2e/linked.spec.ts"));
                Assert.Null(TestScriptSource.Read(_root, "away/secret.ts").Content);
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_missing_too_large_or_binary_file_says_so()
    {
        Write("e2e/huge.spec.ts", new string('x', TestScriptSource.MaxBytes + 1));
        File.WriteAllBytes(Write("e2e/binary.spec.ts", ""), [0x74, 0x00, 0x65]);

        Assert.Equal(TestScriptSource.NotFound, TestScriptSource.Read(_root, "e2e/missing.spec.ts").Error);
        Assert.Equal(TestScriptSource.NotFound, TestScriptSource.Read(_root, "nowhere/missing.spec.ts").Error);
        Assert.Equal(TestScriptSource.TooLarge, TestScriptSource.Read(_root, "e2e/huge.spec.ts").Error);
        Assert.Equal(TestScriptSource.NotAScript, TestScriptSource.Read(_root, "e2e/binary.spec.ts").Error);
    }
}
