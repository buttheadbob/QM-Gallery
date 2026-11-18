using QM_Gallery.Services;

namespace QM_Gallery.Tests;

public sealed class MarkdownRendererTests
{
    private const string Repo = "CometWorks/grid-backups";
    private const string Sha = "fe1c9e0849908b54319749ffc7d3c763829d9476";

    private static readonly MarkdownRenderer Renderer = new();

    [Fact]
    public void RewritesRelativeLinksToThePinnedCommit()
    {
        string html = Renderer.Render("See [the guide](docs/Guide.md) for details.", Repo, Sha);

        Assert.Contains(
            $"href=\"https://github.com/{Repo}/blob/{Sha}/docs/Guide.md\"",
            html);
        Assert.DoesNotContain("mddocs", html);
    }

    [Fact]
    public void RewritesRelativeImagesToRawContent()
    {
        string html = Renderer.Render("![shot](assets/shot.png)", Repo, Sha);

        Assert.Contains(
            $"src=\"https://raw.githubusercontent.com/{Repo}/{Sha}/assets/shot.png\"",
            html);
        Assert.Contains("loading=\"lazy\"", html);
    }

    [Fact]
    public void LeavesAnchorsAndAbsoluteLinksAlone()
    {
        string html = Renderer.Render("[top](#top) and [site](https://example.com)", Repo, Sha);

        Assert.Contains("href=\"#top\"", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void StripsScriptsAndScriptUrls()
    {
        string html = Renderer.Render(
            "<script>alert('xss')</script>\n\n[bad](javascript:alert(1))",
            Repo,
            Sha);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsReadmeHeadingsAndTables()
    {
        string html = Renderer.Render("## Install\n\n| A | B |\n|---|---|\n| 1 | 2 |", Repo, Sha);

        Assert.Contains("<h2", html);
        Assert.Contains("<table>", html);
    }
}
