using System.Text.Json;
using SideHub.Agent;
using SideHub.Cli.Okf;

namespace SideHub.Agent.Tests;

public class DriveTreeTests
{
    private static readonly JsonElement Tree = JsonDocument.Parse("""
        [
          { "id": "f1", "type": "folder", "title": "Paiements", "status": "active", "children": [
            { "id": "p1", "type": "page", "title": "Refund flow (v2)", "slug": "refund-flow", "status": "active", "updatedAt": "2026-11-02",
              "okf": { "valid": true, "type": "Playbook", "description": "How refunds go", "tags": ["oncall"], "status": "draft", "staleAfter": "2026-01-01T00:00:00Z", "trust": "unverified" } },
            { "id": "p2", "type": "page", "title": "Données clients", "status": "active", "updatedAt": "2026-11-03",
              "okf": { "valid": true, "type": "Table", "description": null, "tags": [], "status": "stable", "staleAfter": null, "trust": "human" } },
            { "id": "p3", "type": "page", "title": "Notes", "status": "active", "updatedAt": "2026-11-01" },
            { "id": "p4", "type": "page", "title": "Old", "status": "archived", "updatedAt": "2026-11-01" }
          ] }
        ]
        """).RootElement;

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("/paiements/refund-flow.md", "p1")]
    [InlineData("paiements/refund-flow-v2.md", "p1")]
    [InlineData("/Paiements/donnees-clients.md#joins", "p2")]
    [InlineData("/paiements/", "f1")]
    [InlineData("/paiements/index.md", "f1")]
    public void FindByPath_ResolvesKeptSlugsTitlesAndFolders(string path, string id)
    {
        Assert.Equal(id, DriveTree.FindByPath(Tree, path)?.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("/paiements/old.md")]
    [InlineData("/paiements/missing.md")]
    [InlineData("/")]
    public void FindByPath_ReturnsNullForArchivedOrMissingPages(string path)
    {
        Assert.Null(DriveTree.FindByPath(Tree, path));
    }

    [Fact]
    public void Concepts_FiltersOnTheOkfFactsOnly()
    {
        Assert.Equal(new[] { "p1", "p2" }, Ids(new DriveTree.OkfFilter(null, null, null, false)));
        Assert.Equal(new[] { "p1" }, Ids(new DriveTree.OkfFilter("playbook", null, null, false)));
        Assert.Equal(new[] { "p1" }, Ids(new DriveTree.OkfFilter(null, "OnCall", null, false)));
        Assert.Equal(new[] { "p2" }, Ids(new DriveTree.OkfFilter(null, null, "stable", false)));
        Assert.Equal(new[] { "p1" }, Ids(new DriveTree.OkfFilter(null, null, null, true)));
    }

    [Theory]
    [InlineData("Données ventes", "donnees-ventes")]
    [InlineData("ÉTÉ à Noël", "ete-a-noel")]
    [InlineData("glossaire.md", "glossaire")]
    [InlineData("  -- Q&A: v2 --  ", "q-a-v2")]
    public void Slug_MatchesTheBackendRule(string title, string slug)
    {
        Assert.Equal(slug, DriveTree.Slug(title));
    }

    [Fact]
    public void DriveIndex_ShowsTheTypeAndDescriptionOfConcepts()
    {
        var index = SkillInstaller.BuildDriveIndex(Tree);

        Assert.Contains("p1  \"Refund flow (v2)\"  [Playbook, draft, out of date] \"How refunds go\"", index);
        Assert.Contains("p2  \"Données clients\"  [Table, verified]", index);
        Assert.Contains("p3  \"Notes\"\n", index);
    }

    private static string?[] Ids(DriveTree.OkfFilter filter) =>
        DriveTree.Concepts(Tree, filter, Now).Select(p => p.GetProperty("id").GetString()).ToArray();
}
