namespace QM_Gallery.Models;

public sealed class GallerySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? LastManifestRefresh { get; set; }
    public DateTimeOffset? LastStatsRefresh { get; set; }
    public DateTimeOffset? LastReadmeRefresh { get; set; }
    public List<HubStatus> Hubs { get; set; } = [];
    public List<PluginEntry> Entries { get; set; } = [];
    public List<ModEntry> Mods { get; set; } = [];
    public Dictionary<string, CommitInfo> Commits { get; set; } = [];
    public Dictionary<string, RepoInfo> Repos { get; set; } = [];
    public Dictionary<string, StatsEntry> PulsarStats { get; set; } = [];
    public Dictionary<string, StatsEntry> MagnetarStats { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed record GalleryView(
    GallerySnapshot Snapshot,
    IReadOnlyList<PluginGroup> Plugins,
    IReadOnlyList<ModEntry> Mods,
    IReadOnlyDictionary<string, PluginGroup> BySlug,
    IReadOnlyDictionary<string, PluginGroup> ById,
    int Revision,
    DateTimeOffset BuiltAt)
{
    public static GalleryView Empty { get; } = new(
        new GallerySnapshot(),
        [],
        [],
        new Dictionary<string, PluginGroup>(),
        new Dictionary<string, PluginGroup>(),
        0,
        DateTimeOffset.UtcNow);
}

public sealed class CatalogRequest
{
    public string? Q { get; set; }
    public List<string> Systems { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
    public List<string> Runtimes { get; set; } = [];
    public string Sort { get; set; } = "updated";
    public string Updated { get; set; } = "any";
    public bool Dependencies { get; set; }
    public bool Hidden { get; set; }
    public string? Author { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 36;
}

public sealed class CatalogResult
{
    public IReadOnlyList<PluginGroup> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageCount { get; set; } = 1;
    public Dictionary<string, int> SystemCounts { get; set; } = [];
    public Dictionary<string, int> PlatformCounts { get; set; } = [];
    public Dictionary<string, int> RuntimeCounts { get; set; } = [];
    public Dictionary<string, int> AuthorCounts { get; set; } = [];
}

public sealed class ModsRequest
{
    public string? Q { get; set; }
    public string Sort { get; set; } = "players";
    public bool Hidden { get; set; }
    public string? Author { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 48;
}

public sealed class ModsResult
{
    public IReadOnlyList<ModEntry> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageCount { get; set; } = 1;
    public Dictionary<string, int> AuthorCounts { get; set; } = [];
}
