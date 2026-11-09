using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// Work reviews ("À relire"): what a delegated work changed in the repository, round by round, its diff read live from
/// the agent, and the reviewer's decision (validate, dismiss, ask for a fix) or a second opinion from another CLI.
/// </summary>
public static class ReviewCommands
{
    private const int DefaultLimit = 20;
    private const int MessageMaxLines = 20;
    private const int MessageMaxChars = 2000;

    private static readonly string[] Statuses = ["pending", "validated", "fix-requested", "dismissed"];
    private static readonly string[] Actions = ["validate", "dismiss", "request-fix"];
    private static readonly string[] PeerClis = ["claude", "codex"];

    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var status = GetOption(args, "--status");
        var limitArg = GetOption(args, "--limit");
        var limit = DefaultLimit;
        if ((status is not null && !Statuses.Contains(status))
            || (limitArg is not null && (!int.TryParse(limitArg, out limit) || limit < 1)))
        {
            Console.Error.WriteLine("Usage: sidehub-cli review list [--status pending|validated|fix-requested|dismissed] [--task <id>] [--run <id>] [--limit N]");
            return 1;
        }

        var result = await client.GetReviewsAsync(new SideHubApiClient.ReviewFilter(
            status, GetOption(args, "--task"), GetOption(args, "--run"), limit));

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No reviews found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-14} {"SOURCE",-12} {"LAST ROUND",-24} {"UPDATED",-22} {"TITLE"}");
        Console.WriteLine(new string('-', 140));
        foreach (var review in result.EnumerateArray())
        {
            var source = $"{Prop(review, "source")}/{Prop(review, "provider") ?? "?"}";
            var last = LastRound(review);
            var changes = last is { } round ? $"#{Int(round, "index")} {Changes(round)}" : "-";
            Console.WriteLine($"{Prop(review, "id"),-38} {Prop(review, "status") ?? "-",-14} {source,-12} {changes,-24} " +
                $"{PropDate(review, "updatedAt"),-22} {Truncate(Prop(review, "title") ?? "", 60)}");
        }
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reviewId = Positionals(args).FirstOrDefault();
        if (reviewId is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli review get <reviewId>");
            return 1;
        }

        var review = await client.GetReviewAsync(reviewId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(review));
            return 0;
        }

        var status = Prop(review, "status") ?? "-";
        if (status == "pending" && Prop(review, "pendingSince") is not null)
            status += $" since {PropDate(review, "pendingSince")}";
        Console.WriteLine($"Review:     {Prop(review, "id")}");
        Console.WriteLine($"Title:      {Prop(review, "title")}");
        Console.WriteLine($"Status:     {status}");
        Console.WriteLine($"Source:     {Prop(review, "source")}, CLI {Prop(review, "provider") ?? "?"}");
        Console.WriteLine($"Agent:      {Prop(review, "agentId")}");
        if (LastRound(review) is { } last && (Prop(last, "endBranch") ?? Prop(last, "startBranch")) is { } branch)
            Console.WriteLine($"Branch:     {branch}");
        PrintIfSet(review, "taskId", "Task");
        PrintIfSet(review, "runId", "Run");
        PrintIfSet(review, "workflowExecutionId", "Execution");
        PrintIfSet(review, "stepId", "Step");
        Console.WriteLine($"Updated:    {PropDate(review, "updatedAt")}");

        foreach (var round in Items(review, "rounds"))
            PrintRound(round);

        var decisions = Items(review, "decisions");
        if (decisions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Decisions:");
            foreach (var d in decisions)
            {
                var fix = Prop(d, "fixRunId") is { } fixRun ? $" (fix run {fixRun})" : "";
                var comment = Prop(d, "comment") is { } c ? $": {OneLine(c, 200)}" : "";
                Console.WriteLine($"  {PropDate(d, "at"),-22} {Prop(d, "action")}{WorkflowRoles.OnBehalfOf(d)}{fix}{comment}");
            }
        }

        if (Prop(review, "status") == "pending")
        {
            var id = Prop(review, "id");
            Console.WriteLine();
            Console.WriteLine("Next:");
            Console.WriteLine($"  sidehub-cli review diff {id} [--round N] [--path <file>]");
            Console.WriteLine($"  sidehub-cli review decide {id} validate|dismiss");
            Console.WriteLine($"  sidehub-cli review decide {id} request-fix --comment \"...\"");
            Console.WriteLine($"  sidehub-cli review peer {id} --cli claude|codex");
        }
        return 0;
    }

    public static async Task<int> DiffAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reviewId = Positionals(args, "--round", "--path").FirstOrDefault();
        var roundArg = GetOption(args, "--round");
        var path = GetOption(args, "--path");
        int? round = null;
        if (reviewId is null || (roundArg is not null && (round = ParseRound(roundArg)) is null))
        {
            Console.Error.WriteLine("Usage: sidehub-cli review diff <reviewId> [--round N] [--path <file>]");
            return 1;
        }

        JsonElement? review = null;
        if (round is null)
        {
            review = await client.GetReviewAsync(reviewId);
            if (LastRound(review.Value) is not { } last)
            {
                Console.Error.WriteLine($"Review {reviewId} has no round yet: nothing to diff.");
                return 1;
            }
            round = Int(last, "index");
        }

        string diff;
        try
        {
            diff = await client.GetReviewDiffAsync(reviewId, round.Value, path);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            Console.Error.WriteLine($"The diff of round {round} is too large to send whole: ask for one file with --path <file>.");
            review ??= await client.GetReviewAsync(reviewId);
            var files = Items(review.Value, "rounds").FirstOrDefault(r => Int(r, "index") == round) is { ValueKind: JsonValueKind.Object } r2
                ? Items(r2, "files") : [];
            if (files.Count > 0)
            {
                Console.Error.WriteLine($"Files of round {round}:");
                foreach (var file in files)
                    Console.Error.WriteLine($"  sidehub-cli review diff {reviewId} --round {round} --path {QuoteIfNeeded(Prop(file, "path") ?? "")}");
            }
            return 1;
        }

        if (json)
        {
            // Relaxed escaping: the diff stays readable ('+', '<', quotes in code) and is still valid JSON.
            Console.WriteLine(JsonSerializer.Serialize(new { reviewId, round, path, diff },
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }

        if (diff.Length == 0)
        {
            Console.WriteLine("No difference.");
            return 0;
        }
        Console.Write(diff.EndsWith('\n') ? diff : diff + "\n");
        return 0;
    }

    public static async Task<int> DecideAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positionals = Positionals(args, "--comment");
        var comment = GetOption(args, "--comment");
        var reviewId = positionals.ElementAtOrDefault(0);
        var action = positionals.ElementAtOrDefault(1);
        if (reviewId is null || action is null || !Actions.Contains(action)
            || (action == "request-fix" && string.IsNullOrWhiteSpace(comment)))
        {
            Console.Error.WriteLine("Usage: sidehub-cli review decide <reviewId> validate|dismiss [--comment \"...\"]");
            Console.Error.WriteLine("       sidehub-cli review decide <reviewId> request-fix --comment \"...\"");
            return 1;
        }

        var result = await client.DecideReviewAsync(reviewId, action, comment);
        var review = result.TryGetProperty("review", out var r) ? r : default;

        // A review already decided, or with nothing to review yet, answers 200 as it is: say so instead of claiming a decision.
        if (result.TryGetProperty("unchanged", out var unchanged) && unchanged.ValueKind == JsonValueKind.True)
        {
            if (json)
                Console.WriteLine(SideHubApiClient.Serialize(result));
            else
                Console.Error.WriteLine(Prop(review, "status") is { } status
                    ? $"Review {reviewId} is already {status}: nothing changed."
                    : $"Review {reviewId} has nothing to review yet (its work changed nothing, or its round is still running): nothing changed.");
            return 1;
        }

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine(action switch
        {
            "validate" => $"Review {reviewId} validated.",
            "dismiss" => $"Review {reviewId} dismissed: the code is not touched.",
            _ => Prop(result, "fixRunId") is { } fixRunId
                ? $"Fix asked on review {reviewId}: run {fixRunId} launched (sidehub-cli run get {fixRunId})."
                : $"Fix asked on review {reviewId}: queued on the agent's task queue (sidehub-cli queue show --agent {Prop(review, "agentId")})."
        });
        return 0;
    }

    public static async Task<int> PeerAsync(SideHubApiClient client, string[] args, bool json)
    {
        var reviewId = Positionals(args, "--round", "--cli", "--model").FirstOrDefault();
        var roundArg = GetOption(args, "--round");
        var cli = GetOption(args, "--cli")?.ToLowerInvariant();
        int? round = null;
        if (reviewId is null || cli is null || !PeerClis.Contains(cli)
            || (roundArg is not null && (round = ParseRound(roundArg)) is null))
        {
            Console.Error.WriteLine("Usage: sidehub-cli review peer <reviewId> [--round N] --cli claude|codex [--model <model>]");
            return 1;
        }

        if (round is null)
        {
            var review = await client.GetReviewAsync(reviewId);
            if (LastRound(review) is not { } last)
            {
                Console.Error.WriteLine($"Review {reviewId} has no round yet: nothing to review.");
                return 1;
            }
            round = Int(last, "index");
        }

        var result = await client.RequestPeerReviewAsync(reviewId, round.Value, cli, GetOption(args, "--model"));

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Peer review of round {round} asked to {cli}: run {Prop(result, "runId")}.");
        Console.WriteLine($"Its recommendations appear in: sidehub-cli review get {reviewId}");
        return 0;
    }

    private static void PrintRound(JsonElement round)
    {
        var index = Int(round, "index");
        Console.WriteLine();
        Console.WriteLine($"Round {index} ({(index == 1 ? "work" : "fix")}), {PropDate(round, "startedAt")} to {PropDate(round, "endedAt")}" +
            (Prop(round, "runId") is { } runId ? $", run {runId}" : ""));
        if (Prop(round, "comment") is { } comment)
        {
            Console.WriteLine("  Fix asked:");
            PrintIndented(comment, "    ");
        }
        if (Prop(round, "error") is { } error)
            Console.WriteLine($"  Not measured: {error}");

        var files = Items(round, "files");
        Console.WriteLine($"  Files: {Changes(round)}");
        foreach (var file in files)
        {
            var counts = file.TryGetProperty("binary", out var b) && b.ValueKind == JsonValueKind.True
                ? "binary"
                : $"+{Int(file, "insertions")} -{Int(file, "deletions")}";
            var renamed = Prop(file, "oldPath") is { } oldPath ? $" (from {oldPath})" : "";
            Console.WriteLine($"    {Prop(file, "status"),-10} {counts,-12} {Prop(file, "path")}{renamed}");
        }
        if (IsTrue(round, "filesTruncated"))
            Console.WriteLine("    ... more files (full list with --json)");

        var commits = Items(round, "commits");
        if (commits.Count > 0)
        {
            Console.WriteLine("  Commits:");
            foreach (var commit in commits)
            {
                var sha = Prop(commit, "sha") ?? "";
                Console.WriteLine($"    {(sha.Length > 10 ? sha[..10] : sha)} {Prop(commit, "subject")}");
            }
            if (IsTrue(round, "commitsTruncated"))
                Console.WriteLine("    ... more commits");
        }

        if (Prop(round, "summary") is { } summary)
        {
            Console.WriteLine("  Agent's last message:");
            PrintIndented(Cut(summary), "    ");
        }

        foreach (var peer in Items(round, "peerReviews"))
        {
            var model = Prop(peer, "model") is { } m ? $" ({m})" : "";
            var recommendations = Items(peer, "recommendations");
            Console.WriteLine($"  Peer review {Prop(peer, "id")} by {Prop(peer, "provider")}{model}: {Prop(peer, "status")}" +
                (recommendations.Count > 0 ? $", {recommendations.Count} recommendations" : "") +
                (peer.TryGetProperty("sent", out var sent) && sent.ValueKind == JsonValueKind.Object ? ", sent as a fix" : ""));
            if (Prop(peer, "error") is { } peerError)
                Console.WriteLine($"    Error: {OneLine(peerError, 200)}");
            foreach (var rec in recommendations)
            {
                var where = Prop(rec, "file") is { } file
                    ? Prop(rec, "line") is { } line ? $" {file}:{line}" : $" {file}"
                    : "";
                Console.WriteLine($"    [{Prop(rec, "severity")}]{where} {Prop(rec, "title")}");
            }
        }
    }

    /// <summary>The agent's message, cut to a screen: the whole text is in <c>--json</c>.</summary>
    private static string Cut(string text)
    {
        var lines = text.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        var shown = string.Join("\n", lines.Take(MessageMaxLines));
        if (shown.Length > MessageMaxChars)
            shown = shown[..MessageMaxChars];
        return shown.Length < text.TrimEnd().Length ? shown.TrimEnd() + "\n[... cut, full text with --json]" : shown;
    }

    private static void PrintIndented(string text, string indent)
    {
        foreach (var line in text.ReplaceLineEndings("\n").TrimEnd().Split('\n'))
            Console.WriteLine($"{indent}{line}");
    }

    private static JsonElement? LastRound(JsonElement review)
    {
        var rounds = Items(review, "rounds");
        return rounds.Count == 0 ? null : rounds.MaxBy(r => Int(r, "index"));
    }

    private static string Changes(JsonElement round) =>
        Prop(round, "error") is not null
            ? "not measured"
            : $"{Int(round, "filesChanged")} files +{Int(round, "insertions")} -{Int(round, "deletions")}";

    private static int? ParseRound(string value) => int.TryParse(value, out var round) && round >= 1 ? round : null;

    private static string QuoteIfNeeded(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static void PrintIfSet(JsonElement el, string name, string label)
    {
        if (Prop(el, name) is { } value)
            Console.WriteLine($"{(label + ":").PadRight(12)}{value}");
    }

    private static List<JsonElement> Items(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToList() : [];

    private static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool IsTrue(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string OneLine(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return Truncate(line, max);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    /// <summary>The arguments that are neither a flag nor the value of one of <paramref name="valueFlags"/>.</summary>
    private static List<string> Positionals(string[] args, params string[] valueFlags)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) result.Add(args[i]);
        }
        return result;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
