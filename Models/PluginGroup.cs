namespace QM_Gallery.Models;

/// <summary>A plugin as presented to the gallery: one or more hub descriptors merged together.</summary>
public sealed class PluginGroup
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Description { get; set; } = "";
    public string? RepoKey { get; set; }
    public string? RepoOwner { get; set; }
    public string? Version { get; set; }

    public List<PluginEntry> Entries { get; set; } = [];
    public List<string> Systems { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
    public List<string> Runtimes { get; set; } = [];
    public List<string> DependencyIds { get; set; } = [];
    public List<string> CompanionIds { get; set; } = [];

    public bool IsHidden { get; set; }
    public bool IsDependency { get; set; }
    public bool IsLegacyAlias { get; set; }
    public bool Recommended { get; set; }

    public string? PrimaryId { get; set; }
    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public string? CommitUrl { get; set; }

    public RepoInfo? Repo { get; set; }
    public StatsEntry? PulsarStats { get; set; }
    public StatsEntry? MagnetarStats { get; set; }

    public int PulsarPlayers => PulsarStats?.Players ?? 0;
    public int MagnetarServers => MagnetarStats?.Players ?? 0;
    public int Votes => (PulsarStats?.Upvotes ?? 0) + (PulsarStats?.Downvotes ?? 0);

    /// <summary>Rating on a 0-5 scale. Pulsar reports half-stars (0-10).</summary>
    public double? Rating =>
        PulsarStats is { Rating: > 0 } stats ? Math.Round(stats.Rating / 2d, 1) : null;

    /// <summary>Confidence-weighted rating used for sorting.</summary>
    public double WeightedRating
    {
        get
        {
            double rating = Rating ?? 0;
            int votes = Votes;
            const double priorRating = 4.4;
            const int priorVotes = 8;
            return votes == 0
                ? 0
                : (votes * rating + priorVotes * priorRating) / (votes + priorVotes);
        }
    }

    public bool HasPulsar => Systems.Contains("pulsar");
    public bool HasMagnetar => Systems.Contains("magnetar");
    public bool HasQuasar => Systems.Contains("quasar");
    public bool IsMultiSystem => Systems.Count > 1;

    public string SearchText { get; set; } = "";
}
