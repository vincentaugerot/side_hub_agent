using SideHub.Cli.Okf;

namespace SideHub.Agent.Tests;

public class OkfTests : IDisposable
{
    private readonly string _bundle = Path.Combine(Path.GetTempPath(), "sidehub-okf-tests-" + Guid.NewGuid().ToString("N"));

    public OkfTests() => Directory.CreateDirectory(_bundle);

    public void Dispose()
    {
        try { Directory.Delete(_bundle, recursive: true); } catch { /* best effort */ }
    }

    private const string Concept = """
        ---
        type: Playbook
        title: "Incident response: data freshness alert"
        tags: [oncall, incident]
        status: draft
        generated: { by: claude/opus-5-5, at: 2026-11-19T10:00:00Z }
        verified:
          - { by: human:vincent, at: 2026-11-20T09:00:00Z }
        sources:
          - id: ga4
            resource: https://developers.google.com/analytics
        ---

        # Trigger

        Sharded daily.[^ga4] See [orders](/tables/orders.md).

        ```
        [^not-a-citation]
        ```

        [^ga4]: GA4 export schema
        """;

    [Fact]
    public void A_conformant_concept_has_no_error_nor_warning()
    {
        var check = OkfConcept.Check(Concept);

        Assert.NotNull(check);
        Assert.Empty(check.Value.Errors);
        Assert.Empty(check.Value.Warnings);
    }

    [Fact]
    public void A_plain_page_is_not_checked()
    {
        Assert.Null(OkfConcept.Check("# Notes\n\nNo header."));
        Assert.Null(OkfConcept.Check("---\nA sentence after a rule\n---\n"));
    }

    [Theory]
    [InlineData("---\ntitle: No type\n---\n", "no `type`")]
    [InlineData("---\ntype: [unclosed\n---\n", "not valid YAML")]
    [InlineData("---\ntype: \"\"\n---\n", "no `type`")]
    public void A_header_without_type_or_with_invalid_yaml_is_an_error(string text, string expected)
    {
        var check = OkfConcept.Check(text);

        Assert.Contains(check!.Value.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Fields_written_differently_from_the_spec_are_warnings()
    {
        var check = OkfConcept.Check("""
            ---
            type: Decision
            status: final
            stale_after: next month
            generated: { by: me, at: 2026-11-19 }
            verified: yes
            ---

            See [^missing].
            """);

        Assert.Empty(check!.Value.Errors);
        var warnings = string.Join("\n", check.Value.Warnings);
        Assert.Contains("`status: final`", warnings);
        Assert.Contains("`stale_after: next month`", warnings);
        Assert.Contains("`generated.at: 2026-11-19`", warnings);
        Assert.Contains("`generated.by: me` is not an actor", warnings);
        Assert.Contains("`verified` is not", warnings);
        Assert.Contains("[^missing]", warnings);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_bundle, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Lint_reports_non_conformant_files_and_broken_links()
    {
        Write("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Bundle\n\n* [Tables](tables/) - the tables\n");
        Write("playbooks/freshness.md", Concept);
        Write("tables/orders.md", "---\ntype: BigQuery Table\n---\n\nJoins [customers](./customers.md) and [the playbook](../playbooks/freshness.md).\n");
        Write("tables/index.md", "---\ntype: Index\n---\n");
        Write("tables/untyped.md", "# No header\n");
        Write("log.md", "# Log\n\n## 2026-11-19\n* **Creation**: bundle\n\n## Yesterday\n");
        Write(".claims/ignored.md", "not a concept");

        var report = OkfLint.Lint(_bundle);

        Assert.Equal(3, report.Concepts);
        Assert.Equal(
            new[] { "log.md", "tables/index.md", "tables/untyped.md" },
            report.Errors.Select(e => e.File).OrderBy(f => f, StringComparer.Ordinal));
        var broken = Assert.Single(report.Warnings);
        Assert.Equal("tables/orders.md", broken.File);
        Assert.Contains("./customers.md", broken.Message);
    }

    [Fact]
    public void Lint_flags_a_link_that_leaves_the_bundle()
    {
        Write("a.md", "---\ntype: Reference\n---\n\n[up](../../outside.md)\n");

        var report = OkfLint.Lint(_bundle);

        Assert.Empty(report.Errors);
        Assert.Contains("outside the bundle", Assert.Single(report.Warnings).Message);
    }

    [Fact]
    public void Lint_exits_1_on_an_error_and_0_on_warnings_only()
    {
        Write("a.md", "---\ntype: Reference\n---\n\n[later](/b.md)\n");
        Assert.Equal(0, OkfLint.Run([_bundle]));

        Write("b.md", "no header\n");
        Assert.Equal(1, OkfLint.Run([_bundle, "--json"]));
    }
}
