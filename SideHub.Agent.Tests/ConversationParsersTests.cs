using SideHub.Agent.Conversation;

namespace SideHub.Agent.Tests;

/// <summary>
/// The Conversation mode of a terminal (doc 69) reads the CLI's transcript: lines shaped like those Claude Code 2.1 and
/// Codex 0.166 write, anonymized.
/// </summary>
public class ConversationParsersTests
{
    private const string Cwd = "/work/side_hub";

    /// <summary>The steps of the thread, without the figures of the turns (tested apart).</summary>
    private static List<ConversationEvent> ParseAll(IConversationParser parser, params string[] lines) =>
        lines.SelectMany(parser.Parse).Where(e => e.Kind != ConversationKinds.Turn).ToList();

    private static IEnumerable<ConversationEvent> Steps(IEnumerable<ConversationEvent> events) =>
        events.Where(e => e.Kind != ConversationKinds.Turn);

    private static List<ConversationEvent> Turns(IConversationParser parser, params string[] lines) =>
        lines.SelectMany(parser.Parse).Where(e => e.Kind == ConversationKinds.Turn).ToList();

    // === Claude Code ===

    private const string ClaudeUser =
        """{"parentUuid":null,"isSidechain":false,"type":"user","message":{"role":"user","content":"Ajoute un filtre par statut"},"uuid":"u-1","timestamp":"2026-11-04T08:33:30.591Z"}""";

    private const string ClaudeText =
        """{"parentUuid":"u-1","isSidechain":false,"message":{"model":"claude-opus-5-5","id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"Je regarde la page."}]},"type":"assistant","uuid":"a-1","timestamp":"2026-11-04T08:33:35.000Z"}""";

    private const string ClaudeThinking =
        """{"isSidechain":false,"message":{"id":"msg_1","role":"assistant","content":[{"type":"thinking","thinking":"","signature":"x"}]},"type":"assistant","uuid":"a-0"}""";

    private const string ClaudeBash =
        """{"isSidechain":false,"message":{"id":"msg_1","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"npm test\necho done","description":"Run tests"}}]},"type":"assistant","uuid":"a-2","timestamp":"2026-11-04T08:33:36.000Z"}""";

    private const string ClaudeBashResult =
        """{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Executed 12 of 12 SUCCESS","is_error":false}]},"uuid":"u-2","timestamp":"2026-11-04T08:33:40.000Z"}""";

    private const string ClaudeEdit =
        """{"isSidechain":false,"message":{"id":"msg_2","role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"Edit","input":{"file_path":"/work/side_hub/src/app.ts","old_string":"a = 1","new_string":"a = 2"}}]},"type":"assistant","uuid":"a-3"}""";

    private const string ClaudeEditResult =
        """{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_2","type":"tool_result","content":"The file /work/side_hub/src/app.ts has been updated."}]},"uuid":"u-3"}""";

    [Fact]
    public void Claude_ReadsMessagesAndToolCalls_InOrder()
    {
        var events = ParseAll(new ClaudeConversationParser(Cwd), ClaudeUser, ClaudeThinking, ClaudeText, ClaudeBash);

        Assert.Collection(events,
            e => { Assert.Equal(ConversationKinds.User, e.Kind); Assert.Equal("Ajoute un filtre par statut", e.Text); Assert.Equal("u-1", e.Id); },
            e => { Assert.Equal(ConversationKinds.Assistant, e.Kind); Assert.Equal("Je regarde la page.", e.Text); },
            e =>
            {
                Assert.Equal(ConversationKinds.Tool, e.Kind);
                Assert.Equal("toolu_1", e.Id);
                Assert.Equal(ToolCategories.Run, e.Category);
                Assert.Equal("npm test …", e.Title);
                Assert.Equal(ToolStatuses.Running, e.Status);
            });
        Assert.Equal(new DateTime(2026, 11, 4, 8, 33, 30, 591, DateTimeKind.Utc), events[0].At);
    }

    [Fact]
    public void Claude_ToolResult_SendsTheCallAgain_DoneWithItsOutput()
    {
        var parser = new ClaudeConversationParser(Cwd);
        ParseAll(parser, ClaudeBash);

        var result = Assert.Single(parser.Parse(ClaudeBashResult));

        Assert.Equal("toolu_1", result.Id);
        Assert.Equal(ToolStatuses.Done, result.Status);
        Assert.Equal("Executed 12 of 12 SUCCESS", result.Excerpt);
        Assert.Equal("npm test …", result.Title);
    }

    [Fact]
    public void Claude_ToolError_IsFailed()
    {
        var parser = new ClaudeConversationParser(Cwd);
        ParseAll(parser, ClaudeBash);

        var result = Assert.Single(parser.Parse(ClaudeBashResult.Replace("\"is_error\":false", "\"is_error\":true").Replace("Executed 12 of 12 SUCCESS", "Exit code 1")));

        Assert.Equal(ToolStatuses.Failed, result.Status);
        Assert.Equal("Exit code 1", result.Excerpt);
    }

    [Fact]
    public void Claude_Edit_KeepsItsDiff_AndARelativePath()
    {
        var parser = new ClaudeConversationParser(Cwd);
        var call = Assert.Single(parser.Parse(ClaudeEdit));
        var done = Assert.Single(parser.Parse(ClaudeEditResult));

        Assert.Equal(ToolCategories.Edit, call.Category);
        Assert.Equal("src/app.ts", call.Title);
        Assert.Equal("- a = 1\n+ a = 2", done.Excerpt);
        Assert.Equal(ToolStatuses.Done, done.Status);
    }

    [Fact]
    public void Claude_LeavesOutMetadataSidechainAndInjectedContext()
    {
        var events = ParseAll(new ClaudeConversationParser(Cwd),
            """{"type":"ai-title","aiTitle":"Filtre","sessionId":"s"}""",
            """{"type":"permission-mode","permissionMode":"auto","sessionId":"s"}""",
            ClaudeUser.Replace("\"isSidechain\":false", "\"isSidechain\":true"),
            ClaudeUser.Replace("\"isSidechain\":false", "\"isSidechain\":false,\"isMeta\":true"),
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Total cost: $0.12</local-command-stdout>"},"uuid":"u-9"}""",
            "{\"type\":\"user\", half written");

        Assert.Empty(events);
    }

    [Fact]
    public void Claude_SlashCommandAndInterruption_AreShownAsSuch()
    {
        var events = ParseAll(new ClaudeConversationParser(Cwd),
            """{"type":"user","message":{"role":"user","content":"<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args>42</command-args>"},"uuid":"u-5"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"uuid":"u-6"}""");

        Assert.Collection(events,
            e => { Assert.Equal(ConversationKinds.User, e.Kind); Assert.Equal("/review 42", e.Text); },
            e => { Assert.Equal(ConversationKinds.Note, e.Kind); Assert.Equal("interrupted", e.Label); });
    }

    [Fact]
    public void Claude_UnknownContentPart_IsAnUnknownEvent_ReportedOnce()
    {
        var parser = new ClaudeConversationParser(Cwd);
        var line = """{"type":"assistant","message":{"id":"msg_9","role":"assistant","content":[{"type":"hologram","data":"x"}]},"uuid":"a-9"}""";

        var first = Assert.Single(parser.Parse(line));
        Assert.Equal(ConversationKinds.Unknown, first.Kind);
        Assert.Equal("assistant:hologram", first.Label);
        Assert.Equal(["assistant:hologram"], parser.TakeUnrecognized());

        parser.Parse(line.Replace("a-9", "a-10"));
        Assert.Empty(parser.TakeUnrecognized());
    }

    private static readonly string[] Images = ["/work/side_hub/.sidehub-images"];

    [Fact]
    public void Claude_TaskNotification_IsANoteWithItsSummaryAndStatus()
    {
        var events = ParseAll(new ClaudeConversationParser(Cwd),
            """{"type":"user","promptSource":"system","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>b1</task-id>\n<status>completed</status>\n<summary>Background command \"Run tests\" completed (exit code 0)</summary>\n</task-notification>"},"uuid":"u-7"}""",
            """{"type":"user","message":{"role":"user","content":"<task-notification>\n<task-id>b2</task-id>\n<summary>Monitor event: \"probe\"</summary>\n<event>[Monitor expired]</event>\n</task-notification>"},"uuid":"u-8"}""",
            """{"type":"user","message":{"role":"user","content":"<task-notification>\n<status>killed</status>\n<event>stopped</event>\n</task-notification>"},"uuid":"u-9"}""");

        Assert.Collection(events,
            e =>
            {
                Assert.Equal(ConversationKinds.Note, e.Kind);
                Assert.Equal("task", e.Label);
                Assert.Equal("Background command \"Run tests\" completed (exit code 0)", e.Text);
                Assert.Equal(ToolStatuses.Done, e.Status);
            },
            e => { Assert.Equal("Monitor event: \"probe\"", e.Text); Assert.Null(e.Status); },
            e => { Assert.Equal("stopped", e.Text); Assert.Equal(ToolStatuses.Failed, e.Status); });
    }

    [Fact]
    public void Claude_ImagesPastedFromSideHub_AreTakenOutOfTheText()
    {
        var events = ParseAll(new ClaudeConversationParser(Cwd, Images),
            """{"type":"user","message":{"role":"user","content":"[Image #1: /work/side_hub/.sidehub-images/17937-ab.jpg] [Image #2: /work/side_hub/.sidehub-images/17938-cd.png] Le champ est compressé"},"uuid":"u-10"}""",
            """{"type":"user","message":{"role":"user","content":"[Image #3: /work/side_hub/.sidehub-images/17939-ef.jpg] "},"uuid":"u-11"}""",
            """{"type":"user","message":{"role":"user","content":"[Image #4: /etc/passwd.jpg] [Image #5: /work/side_hub/.sidehub-images/../x.jpg] ailleurs"},"uuid":"u-12"}""");

        Assert.Collection(events,
            e =>
            {
                Assert.Equal("Le champ est compressé", e.Text);
                Assert.Equal(["17937-ab.jpg", "17938-cd.png"], e.Images);
            },
            e => { Assert.Null(e.Text); Assert.Equal(["17939-ef.jpg"], e.Images); },
            e => { Assert.Null(e.Images); Assert.StartsWith("[Image #4: /etc/passwd.jpg]", e.Text); });
    }

    [Fact]
    public void Claude_PastedBlock_ShowsAsTheUserWroteIt()
    {
        var events = ParseAll(new ClaudeConversationParser(Cwd),
            """{"type":"user","message":{"role":"user","content":"\n\n<pasted_content id=\"78fc\">\nPremière ligne\nDeuxième ligne\n</pasted_content id=\"78fc\">\n"},"uuid":"u-13"}""",
            """{"type":"user","message":{"role":"user","content":"ça met <\\pasted_content id=\"78fc\"> dans le fil"},"uuid":"u-14"}""");

        Assert.Collection(events,
            e => Assert.Equal("Première ligne\nDeuxième ligne", e.Text),
            e => Assert.Equal("ça met <pasted_content id=\"78fc\"> dans le fil", e.Text));
    }

    private static string CodexTokens(long total, string at) =>
        "{\"timestamp\":\"" + at + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1,\"output_tokens\":"
        + total + "},\"last_token_usage\":{\"output_tokens\":1}}}}";

    [Fact]
    public void Codex_Turn_CountsWhatTheSessionTotalGrewBy()
    {
        var turns = Turns(new CodexConversationParser(Cwd),
            CodexTokens(200, "2026-11-04T10:00:00Z"),
            CodexItem("""{"type":"UserMessage","id":"um-9","content":[{"type":"text","text":"Relis"}]}""").Replace("2026-11-04T07:00:39.809Z", "2026-11-04T10:00:10Z"),
            CodexTokens(260, "2026-11-04T10:00:30Z"),
            CodexTokens(260, "2026-11-04T10:00:31Z"),
            CodexTokens(300, "2026-11-04T10:00:50Z"));

        Assert.Equal("turn:um-9", turns[0].Id);
        Assert.Equal(0, turns[0].Tokens);
        Assert.Equal(100, turns[^1].Tokens);
        Assert.Equal(new DateTime(2026, 11, 4, 10, 0, 50, DateTimeKind.Utc), turns[^1].EndedAt);
    }

    [Fact]
    public void Codex_ImagesPastedFromSideHub_AreTakenOutOfTheUserText()
    {
        var message = Assert.Single(ParseAll(new CodexConversationParser(Cwd, Images), CodexItem(
            """{"type":"UserMessage","id":"um-2","content":[{"type":"text","text":"[Image #1: /work/side_hub/.sidehub-images/1-a.webp] Regarde"}]}""")));

        Assert.Equal("Regarde", message.Text);
        Assert.Equal(["1-a.webp"], message.Images);
    }

    [Fact]
    public void Claude_Turn_CountsEachResponseOnce_FromTheUserMessage()
    {
        var turns = Turns(new ClaudeConversationParser(Cwd),
            """{"type":"assistant","message":{"id":"m-0","role":"assistant","content":[{"type":"text","text":"avant"}],"usage":{"output_tokens":999}},"uuid":"a-0","timestamp":"2026-11-04T10:00:00Z"}""",
            """{"type":"user","message":{"role":"user","content":"Corrige le test"},"uuid":"u-20","timestamp":"2026-11-04T10:00:10Z"}""",
            """{"type":"assistant","message":{"id":"m-1","role":"assistant","content":[{"type":"thinking","thinking":""}],"usage":{"output_tokens":120}},"uuid":"a-1","timestamp":"2026-11-04T10:00:20Z"}""",
            """{"type":"assistant","message":{"id":"m-1","role":"assistant","content":[{"type":"text","text":"Je lance les tests."}],"usage":{"output_tokens":120}},"uuid":"a-2","timestamp":"2026-11-04T10:00:21Z"}""",
            """{"type":"assistant","message":{"id":"m-2","role":"assistant","content":[{"type":"text","text":"C'est corrigé."}],"usage":{"output_tokens":80}},"uuid":"a-3","timestamp":"2026-11-04T10:01:52Z"}""");

        Assert.All(turns, t => Assert.Equal("turn:u-20", t.Id));
        var last = turns[^1];
        Assert.Equal(200, last.Tokens);
        Assert.Equal(new DateTime(2026, 11, 4, 10, 0, 10, DateTimeKind.Utc), last.At);
        Assert.Equal(new DateTime(2026, 11, 4, 10, 1, 52, DateTimeKind.Utc), last.EndedAt);
    }

    // === Codex ===

    private static string CodexItem(string item, string kind = "item_completed") =>
        $$$"""{"timestamp":"2026-11-04T07:00:39.809Z","type":"event_msg","payload":{"type":"{{{kind}}}","thread_id":"t","turn_id":"r","item":{{{item}}}}}""";

    [Fact]
    public void Codex_ReadsMessagesAndCommands()
    {
        var events = ParseAll(new CodexConversationParser(Cwd),
            """{"timestamp":"2026-11-04T07:00:39.643Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions"}]}}""",
            CodexItem("""{"type":"UserMessage","id":"um-1","client_id":"c","content":[{"type":"text","text":"Relis le dernier commit","text_elements":[]}]}"""),
            CodexItem("""{"type":"Reasoning","id":"rs-1","summary_text":[],"raw_content":[]}"""),
            CodexItem("""{"type":"CommandExecution","id":"exec-1","command":["/bin/bash","-lc","git show --stat HEAD"],"cwd":"file:///work/side_hub","parsed_cmd":[{"type":"unknown","cmd":"git show --stat HEAD"}],"status":"completed","aggregated_output":"1 file changed","exit_code":0}"""),
            CodexItem("""{"type":"CommandExecution","id":"exec-2","command":["/bin/bash","-lc","cat src/app.ts"],"parsed_cmd":[{"type":"read","cmd":"cat src/app.ts","name":"app.ts","path":"/work/side_hub/src/app.ts"}],"status":"completed","aggregated_output":"a = 2","exit_code":0}"""),
            CodexItem("""{"type":"AgentMessage","id":"msg-1","content":[{"type":"Text","text":"Il manque un cas de test."}],"phase":"final"}"""));

        Assert.Collection(events,
            e => { Assert.Equal(ConversationKinds.User, e.Kind); Assert.Equal("Relis le dernier commit", e.Text); },
            e =>
            {
                Assert.Equal(ConversationKinds.Tool, e.Kind);
                Assert.Equal(ToolCategories.Run, e.Category);
                Assert.Equal("git show --stat HEAD", e.Title);
                Assert.Equal(ToolStatuses.Done, e.Status);
                Assert.Equal("1 file changed", e.Excerpt);
            },
            e => { Assert.Equal(ToolCategories.Read, e.Category); Assert.Equal("src/app.ts", e.Title); },
            e => { Assert.Equal(ConversationKinds.Assistant, e.Kind); Assert.Equal("Il manque un cas de test.", e.Text); });
    }

    [Fact]
    public void Codex_CommandStartedThenFailed_KeepsItsId()
    {
        var parser = new CodexConversationParser(Cwd);
        var started = Assert.Single(parser.Parse(CodexItem("""{"type":"CommandExecution","id":"exec-3","command":["/bin/bash","-lc","npm test"],"parsed_cmd":[{"type":"unknown","cmd":"npm test"}],"status":"in_progress"}""", "item_started")));
        var failed = Assert.Single(parser.Parse(CodexItem("""{"type":"CommandExecution","id":"exec-3","command":["/bin/bash","-lc","npm test"],"parsed_cmd":[{"type":"unknown","cmd":"npm test"}],"status":"completed","aggregated_output":"1 failing","exit_code":1}""")));

        Assert.Equal(ToolStatuses.Running, started.Status);
        Assert.Equal("exec-3", failed.Id);
        Assert.Equal(ToolStatuses.Failed, failed.Status);
    }

    [Fact]
    public void Codex_FileChange_ListsItsFilesWithTheirChange()
    {
        var change = Assert.Single(new CodexConversationParser(Cwd).Parse(CodexItem(
            """{"type":"FileChange","id":"fc-1","changes":{"/work/side_hub/src/new.ts":{"type":"add","content":"export const a = 1;\n"}},"status":"completed","stdout":"Success.","stderr":""}""")));

        Assert.Equal(ToolCategories.Edit, change.Category);
        Assert.Equal("src/new.ts", change.Title);
        Assert.Equal("+ export const a = 1;", change.Excerpt);
    }

    [Fact]
    public void Codex_UnknownItem_IsAnUnknownEvent_PausesAreLeftOut()
    {
        var parser = new CodexConversationParser(Cwd);
        var events = ParseAll(parser,
            CodexItem("""{"type":"Extension","kind":"clock.sleep","id":"x-1","durationMs":20000}"""),
            CodexItem("""{"type":"Teleport","id":"x-2"}"""));

        var unknown = Assert.Single(events);
        Assert.Equal(ConversationKinds.Unknown, unknown.Kind);
        Assert.Equal("Teleport", unknown.Label);
        Assert.Equal(["Teleport"], parser.TakeUnrecognized());
    }

    // === Feed ===

    [Fact]
    public void Feed_ReadsOnlyNewLines_AndSendsAChangedCallOnce_SecretsMasked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conversation-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, ClaudeUser.Replace("Ajoute un filtre par statut", "Mon jeton est s3cr3t") + "\n");
            var feed = new ConversationFeed("claude", "s", path, new ClaudeConversationParser(Cwd), t => t?.Replace("s3cr3t", "***"));

            Assert.Equal("Mon jeton est ***", Assert.Single(Steps(feed.Poll())).Text);
            Assert.Empty(feed.Poll());

            File.AppendAllText(path, ClaudeBash + "\n" + ClaudeBashResult + "\n");
            var changed = Assert.Single(Steps(feed.Poll()));
            Assert.Equal(ToolStatuses.Done, changed.Status);

            Assert.Equal(["u-1", "toolu_1"], Steps(feed.Snapshot()).Select(e => e.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Feed_KeepsTheLastEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conversation-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(0, ConversationFeed.MaxEvents + 5).Select(i => ClaudeUser.Replace("u-1", $"u-{i}")));
            var feed = new ConversationFeed("claude", "s", path, new ClaudeConversationParser(Cwd), t => t);
            feed.Poll();

            var snapshot = feed.Snapshot();
            // Each message brings the figures of its turn: 205 messages, the last 100 with theirs
            Assert.Equal(ConversationFeed.MaxEvents, snapshot.Count);
            Assert.Equal("u-105", snapshot[0].Id);
            Assert.Equal("turn:u-105", snapshot[1].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
