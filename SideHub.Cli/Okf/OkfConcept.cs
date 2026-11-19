using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SideHub.Cli.Okf;

/// <summary>
/// Checks of an Open Knowledge Format concept (OKF v0.2, GoogleCloudPlatform/knowledge-catalog okf/SPEC.md): a Markdown
/// file whose YAML front matter carries at least a non-empty <c>type</c>. Only reads: nothing here rewrites a page.
/// </summary>
public static partial class OkfConcept
{
    /// <summary>Values of <c>status</c> (OKF §5.4).</summary>
    private static readonly string[] Statuses = ["draft", "stable", "deprecated"];

    /// <summary>A leading front matter block between <c>---</c> lines.</summary>
    [GeneratedRegex(@"\A﻿?---[ \t]*\r?\n(?<yaml>[\s\S]*?)\r?\n(?:---|\.\.\.)[ \t]*(?:\r?\n|\z)")]
    private static partial Regex FrontMatterBlock();

    /// <summary>First line of a block of keys (<c>type: Playbook</c>), not prose after a rule.</summary>
    [GeneratedRegex(@"^[A-Za-z_][\w.-]*[ \t]*:(?:\s|$)")]
    private static partial Regex FirstKey();

    /// <summary>
    /// The front matter of a Markdown text and the body after it, or null when the text has none. Only a block that
    /// opens on a key counts, as the SideHub Mémoire reads it: a page that starts with a rule then prose is plain.
    /// </summary>
    public static (string Yaml, string Body)? SplitFrontMatter(string text)
    {
        var match = FrontMatterBlock().Match(text);
        if (!match.Success) return null;
        var yaml = match.Groups["yaml"].Value;
        var first = yaml.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (first is null || !FirstKey().IsMatch(first)) return null;
        return (yaml, text[match.Length..]);
    }

    /// <summary>The front matter read as a YAML mapping, or the reason it cannot be.</summary>
    public static YamlMappingNode? ReadMapping(string yaml, out string? error)
    {
        error = null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                error = "the front matter is not a list of keys";
                return null;
            }
            return mapping;
        }
        catch (YamlException ex)
        {
            // Line numbers of the file: the front matter starts on line 2
            error = $"the front matter is not valid YAML (line {ex.Start.Line + 1}: {ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// What is wrong with a concept: <c>Errors</c> make it non-conformant (OKF §11: parseable front matter, non-empty
    /// <c>type</c>), <c>Warnings</c> are fields written differently from the spec. Null when the text has no front
    /// matter at all (a plain page, not an OKF concept).
    /// </summary>
    public static (List<string> Errors, List<string> Warnings)? Check(string text)
    {
        var split = SplitFrontMatter(text);
        if (split is null) return null;
        return CheckFrontMatter(split.Value.Yaml, split.Value.Body);
    }

    public static (List<string> Errors, List<string> Warnings) CheckFrontMatter(string yaml, string body)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var mapping = ReadMapping(yaml, out var error);
        if (mapping is null)
        {
            errors.Add(error!);
            return (errors, warnings);
        }

        if (Scalar(mapping, "type") is not { Length: > 0 })
            errors.Add("the front matter has no `type` (required by OKF: Playbook, Decision, Reference, Module...)");

        if (Scalar(mapping, "status") is { } status && !Statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            warnings.Add($"`status: {status}` is not draft, stable or deprecated");

        if (Scalar(mapping, "stale_after") is { } staleAfter && !IsInstant(staleAfter))
            warnings.Add($"`stale_after: {staleAfter}` is not an ISO 8601 date-time with its offset (2026-12-31T00:00:00Z)");

        if (Child(mapping, "generated") is { } generated)
            CheckStamps("generated", generated, warnings, single: true);
        if (Child(mapping, "verified") is { } verified)
            CheckStamps("verified", verified, warnings, single: false);

        if (Child(mapping, "tags") is { } tags && tags is not YamlSequenceNode)
            warnings.Add("`tags` is not a list ([a, b])");

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        if (Child(mapping, "sources") is { } sources)
        {
            if (sources is not YamlSequenceNode list)
                warnings.Add("`sources` is not a list");
            else
            {
                var index = 0;
                foreach (var entry in list)
                {
                    index++;
                    if (entry is not YamlMappingNode source || Scalar(source, "resource") is not { Length: > 0 })
                    {
                        warnings.Add($"source {index} has no `resource`");
                        continue;
                    }
                    if (Scalar(source, "id") is { Length: > 0 } id) sourceIds.Add(id);
                }
            }
        }

        foreach (var label in Citations(body).Distinct())
            if (!sourceIds.Contains(label))
                warnings.Add($"the citation [^{label}] names no `sources[].id`");

        return (errors, warnings);
    }

    [GeneratedRegex(@"\[\^([^\]\s]+)\](?!:)")]
    private static partial Regex Citation();

    /// <summary>Labels of the footnote citations of a body, outside code blocks.</summary>
    public static IEnumerable<string> Citations(string body) =>
        OutsideCode(body).SelectMany(line => Citation().Matches(line).Select(m => m.Groups[1].Value));

    /// <summary>Lines of a Markdown body that are not in a fenced code block.</summary>
    public static IEnumerable<string> OutsideCode(string body)
    {
        string? fence = null;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var marker = trimmed.StartsWith("```") ? "```" : trimmed.StartsWith("~~~") ? "~~~" : null;
            if (marker is not null && (fence is null || fence == marker))
            {
                fence = fence is null ? marker : null;
                continue;
            }
            if (fence is null) yield return line;
        }
    }

    /// <summary><c>{ by, at }</c>, or for <c>verified</c> a list of them (a bare mapping is one check, OKF §5.2).</summary>
    private static void CheckStamps(string key, YamlNode node, List<string> warnings, bool single)
    {
        List<YamlNode>? stamps = node switch
        {
            YamlMappingNode m => [m],
            YamlSequenceNode s when !single => s.Children.ToList(),
            _ => null,
        };
        if (stamps is null)
        {
            warnings.Add($"`{key}` is not {(single ? "a { by, at } mapping" : "a { by, at } mapping or a list of them")}");
            return;
        }
        foreach (var stamp in stamps)
        {
            if (stamp is not YamlMappingNode m || Scalar(m, "by") is not { Length: > 0 } by)
            {
                warnings.Add($"`{key}` has an entry without `by`");
                continue;
            }
            if (Scalar(m, "at") is { } at && !IsInstant(at))
                warnings.Add($"`{key}.at: {at}` is not an ISO 8601 date-time with its offset (2026-11-19T10:00:00Z)");
            if (!by.Contains('/') && !by.Contains(':'))
                warnings.Add($"`{key}.by: {by}` is not an actor (<cli>/<model>, human:<id> or process:<id>)");
        }
    }

    /// <summary>An ISO 8601 date-time with an explicit offset (OKF §5).</summary>
    public static bool IsInstant(string value) =>
        value.Contains('T') && (value.EndsWith('Z') || value.EndsWith("z") || Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$"))
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static YamlNode? Child(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    public static string? Scalar(YamlMappingNode mapping, string key) =>
        Child(mapping, key) is YamlScalarNode { Value: { } value } ? value.Trim() : null;
}
