using QM_Gallery.Models;
using QM_Gallery.Services;

namespace QM_Gallery.Tests;

public sealed class CatalogQueryEngineTests
{
    private static PluginGroup Group(
        string name,
        string author = "Author",
        string system = "pulsar",
        bool hidden = false,
        int players = 0,
        int rating = 0,
        int votes = 0,
        DateTimeOffset? updated = null,
        params string[] platforms)
    {
        var entry = new PluginEntry
        {
            HubKey = system,
            Id = name.ToLowerInvariant(),
            RepoKey = $"Owner/{name.Replace(" ", "", StringComparison.Ordinal)}",
            FriendlyName = name,
            Author = author,
            Platforms = platforms.ToList()
        };

        var group = new PluginGroup
        {
            Slug = Text.Kebab(name),
            Name = name,
            Author = author,
            Tooltip = $"{name} plugin",
            Description = $"{name} description",
            Systems = [system],
            Entries = [entry],
            IsHidden = hidden,
            Updated = updated,
            PulsarStats = votes > 0 || players > 0
                ? new StatsEntry { Players = players, Rating = rating, Upvotes = votes, Downvotes = 0 }
                : null
        };

        group.SearchText = $"{name} {author} {group.Tooltip} {group.Description}".ToLowerInvariant();
        return group;
    }

    [Fact]
    public void HiddenPluginsAreExcludedByDefault()
    {
        var groups = new[]
        {
            Group("Visible"),
            Group("Hidden", hidden: true)
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest());

        Assert.Equal(1, result.Total);
        Assert.Equal("Visible", result.Items[0].Name);
    }

    [Fact]
    public void HiddenPluginsCanBeIncluded()
    {
        var groups = new[]
        {
            Group("Visible"),
            Group("Hidden", hidden: true)
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest { Hidden = true });

        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void FiltersBySystem()
    {
        var groups = new[]
        {
            Group("Client", system: "pulsar"),
            Group("Server", system: "magnetar")
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest
        {
            Systems = ["magnetar"]
        });

        Assert.Equal("Server", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void EmptyPlatformMeansEveryPlatform()
    {
        var groups = new[]
        {
            Group("AllPlatforms"),
            Group("LinuxOnly", platforms: "Linux")
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest
        {
            Platforms = ["Windows"]
        });

        Assert.Equal("AllPlatforms", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void SearchRequiresEveryToken()
    {
        var groups = new[]
        {
            Group("Performance Optimizer"),
            Group("Inventory Search")
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest { Q = "performance optimizer" });

        Assert.Equal("Performance Optimizer", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void FiltersByUpdateWindow()
    {
        var groups = new[]
        {
            Group("Fresh", updated: DateTimeOffset.UtcNow.AddDays(-2)),
            Group("Old", updated: DateTimeOffset.UtcNow.AddDays(-200))
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest { Updated = "30" });

        Assert.Equal("Fresh", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void SortsByConfidenceWeightedRating()
    {
        var groups = new[]
        {
            Group("OneVote", rating: 10, votes: 1),
            Group("ManyVotes", rating: 10, votes: 100)
        };

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest { Sort = "rating" });

        Assert.Equal("ManyVotes", result.Items[0].Name);
    }

    [Fact]
    public void PaginatesResults()
    {
        PluginGroup[] groups = Enumerable.Range(1, 40)
            .Select(i => Group($"Plugin {i:00}"))
            .ToArray();

        CatalogResult result = CatalogQueryEngine.Query(groups, new CatalogRequest
        {
            Sort = "name",
            Page = 2,
            PageSize = 12
        });

        Assert.Equal(40, result.Total);
        Assert.Equal(4, result.PageCount);
        Assert.Equal(12, result.Items.Count);
        Assert.Equal("Plugin 13", result.Items[0].Name);
    }

    [Fact]
    public void FiltersModsByTextAndSortsByPlayers()
    {
        var mods = new[]
        {
            new ModEntry { Id = "1", Name = "Low", Author = "A", SearchText = "low a", Stats = new StatsEntry { Players = 2 } },
            new ModEntry { Id = "2", Name = "High", Author = "A", SearchText = "high a", Stats = new StatsEntry { Players = 20 } }
        };

        ModsResult result = CatalogQueryEngine.QueryMods(mods, new ModsRequest { Q = "high", Sort = "players" });

        Assert.Equal("High", Assert.Single(result.Items).Name);
    }
}
