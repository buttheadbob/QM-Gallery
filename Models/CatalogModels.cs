namespace QM_Gallery.Models;

public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public string HubKey { get; set; } = "";
    public string HubName { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestBlobUrl { get; set; } = "";
    public string ManifestRawUrl { get; set; } = "";
    public string RawXml { get; set; } = "";
    public StatsEntry? Stats { get; set; }

    public string? WorkshopUrl =>
        IsWorkshopId ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={Id}" : null;

    public bool IsWorkshopId => Id.Length > 0 && Id.All(char.IsAsciiDigit);

    public string SearchText { get; set; } = "";
}

public sealed class StatsEntry
{
    public int Players { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }
    public int Rating { get; set; }
    public bool Tried { get; set; }
    public int Vote { get; set; }

    public int Votes => Upvotes + Downvotes;
    public double? Stars => Rating > 0 ? Math.Round(Rating / 2d, 1) : null;
}

public sealed class CommitInfo
{
    public string Sha { get; set; } = "";
    public string RepoKey { get; set; } = "";
    public DateTimeOffset? Date { get; set; }
    public string Message { get; set; } = "";
    public string? AuthorName { get; set; }
    public string? AuthorLogin { get; set; }
    public string Source { get; set; } = "patch";
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Failed { get; set; }
}

public sealed class RepoInfo
{
    public string RepoKey { get; set; } = "";
    public int Stars { get; set; }
    public int Forks { get; set; }
    public int OpenIssues { get; set; }
    public string? License { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public bool Archived { get; set; }
    public bool Disabled { get; set; }
    public string? DefaultBranch { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Topics { get; set; } = [];
}

public sealed class HubStatus
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string? CommitSha { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    public int PluginCount { get; set; }
    public int ModCount { get; set; }
    public string? Error { get; set; }
}
