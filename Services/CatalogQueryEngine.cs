using QM_Gallery.Models;

namespace QM_Gallery.Services;

public static class CatalogQueryEngine
{
    private static readonly string[] KnownPlatforms = ["Windows", "Linux", "Proton"];
    private static readonly string[] KnownRuntimes = ["CoreCLR", "CLR", "Mono", "NETCoreApp"];

    public static CatalogResult Query(IReadOnlyList<PluginGroup> all, CatalogRequest request)
    {
        IEnumerable<PluginGroup> baseSet = all.Where(g => request.Hidden || !g.IsHidden);

        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            baseSet = baseSet.Where(g =>
                string.Equals(g.Author, request.Author, StringComparison.OrdinalIgnoreCase));
        }

        string[] tokens = Tokenize(request.Q);
        List<PluginGroup> candidates = baseSet.ToList();

        var scored = new List<(PluginGroup Group, int Score)>(candidates.Count);
        foreach (PluginGroup group in candidates)
        {
            int score = SearchScore(group, tokens);
            if (score < 0)
                continue;

            if (request.Systems.Count > 0
                && !request.Systems.Any(s => group.Systems.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (request.Platforms.Count > 0
                && !request.Platforms.Any(p => MatchesPlatform(group, p)))
            {
                continue;
            }

            if (request.Runtimes.Count > 0
                && !request.Runtimes.Any(r => MatchesRuntime(group, r)))
            {
                continue;
            }

            if (request.Dependencies
                && group.DependencyIds.Count == 0
                && group.CompanionIds.Count == 0)
            {
                continue;
            }

            if (request.Updated is "30" or "90" or "365")
            {
                int days = int.Parse(request.Updated);
                if (group.Updated is null || group.Updated < DateTimeOffset.UtcNow.AddDays(-days))
                    continue;
            }

            scored.Add((group, score));
        }

        IEnumerable<PluginGroup> ordered = Sort(scored, request.Sort, tokens.Length > 0);

        int pageSize = Math.Clamp(request.PageSize, 12, 96);
        int total = scored.Count;
        int pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        int page = Math.Clamp(request.Page, 1, pageCount);

        return new CatalogResult
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = total,
            Page = page,
            PageCount = pageCount,
            SystemCounts = SystemCounts(candidates),
            PlatformCounts = PlatformCounts(candidates),
            RuntimeCounts = RuntimeCounts(candidates),
            AuthorCounts = candidates
                .GroupBy(g => g.Author, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public static ModsResult QueryMods(IReadOnlyList<ModEntry> all, ModsRequest request)
    {
        IEnumerable<ModEntry> query = all.Where(m => request.Hidden || !m.Hidden);

        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            query = query.Where(m =>
                string.Equals(m.Author, request.Author, StringComparison.OrdinalIgnoreCase));
        }

        string[] tokens = Tokenize(request.Q);
        List<ModEntry> items = query.Where(m => MatchesText(m.SearchText, tokens)).ToList();

        IEnumerable<ModEntry> ordered = request.Sort switch
        {
            "name" => items.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            "rating" => items
                .OrderByDescending(m => m.Stats?.Rating ?? 0)
                .ThenByDescending(m => m.Stats?.Votes ?? 0)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            "votes" => items
                .OrderByDescending(m => m.Stats?.Votes ?? 0)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            _ => items
                .OrderByDescending(m => m.Stats?.Players ?? 0)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
        };

        int pageSize = Math.Clamp(request.PageSize, 12, 96);
        int total = items.Count;
        int pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        int page = Math.Clamp(request.Page, 1, pageCount);

        return new ModsResult
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = total,
            Page = page,
            PageCount = pageCount,
            AuthorCounts = items
                .GroupBy(m => m.Author, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string[] Tokenize(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(
                [' ', '\t', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .Distinct()
                .Take(8)
                .ToArray();

    private static bool MatchesText(string haystack, string[] tokens) =>
        tokens.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static int SearchScore(PluginGroup group, string[] tokens)
    {
        if (tokens.Length == 0)
            return 0;

        string name = group.Name.ToLowerInvariant();
        string author = group.Author.ToLowerInvariant();
        string tooltip = group.Tooltip.ToLowerInvariant();
        string description = group.Description.ToLowerInvariant();
        string repo = group.RepoKey?.ToLowerInvariant() ?? "";
        string ids = string.Join(' ', group.Aliases).ToLowerInvariant();

        int score = 0;
        foreach (string token in tokens)
        {
            int tokenScore = 0;
            if (name.StartsWith(token, StringComparison.Ordinal))
                tokenScore += 400;
            if (name.Contains(token, StringComparison.Ordinal))
                tokenScore += 200;
            if (author.Contains(token, StringComparison.Ordinal))
                tokenScore += 90;
            if (tooltip.Contains(token, StringComparison.Ordinal))
                tokenScore += 60;
            if (repo.Contains(token, StringComparison.Ordinal))
                tokenScore += 45;
            if (ids.Contains(token, StringComparison.Ordinal))
                tokenScore += 35;
            if (description.Contains(token, StringComparison.Ordinal))
                tokenScore += 15;

            if (tokenScore == 0)
                return -1;

            score += tokenScore;
        }

        return score;
    }

    private static IEnumerable<PluginGroup> Sort(
        List<(PluginGroup Group, int Score)> scored,
        string sort,
        bool hasQuery)
    {
        string mode = string.IsNullOrWhiteSpace(sort)
            ? hasQuery ? "relevance" : "updated"
            : sort.ToLowerInvariant();

        return mode switch
        {
            "relevance" when hasQuery => scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Group.Updated ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.Group.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Group),
            "name" => scored
                .Select(x => x.Group)
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "author" => scored
                .Select(x => x.Group)
                .OrderBy(g => g.Author, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "rating" => scored
                .Select(x => x.Group)
                .OrderByDescending(g => g.Votes > 0 ? g.WeightedRating : 0)
                .ThenByDescending(g => g.PulsarPlayers)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "players" => scored
                .Select(x => x.Group)
                .OrderByDescending(g => g.PulsarPlayers)
                .ThenByDescending(g => g.MagnetarServers)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "stars" => scored
                .Select(x => x.Group)
                .OrderByDescending(g => g.Repo?.Stars ?? 0)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            _ => scored
                .Select(x => x.Group)
                .OrderByDescending(g => g.Updated ?? DateTimeOffset.MinValue)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool MatchesPlatform(PluginGroup group, string platform) =>
        group.Entries.Any(e =>
            e.Platforms.Count == 0
            || e.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesRuntime(PluginGroup group, string runtime) =>
        group.Entries.Any(e =>
            e.Runtimes.Count == 0
            || e.Runtimes.Contains(runtime, StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, int> SystemCounts(IEnumerable<PluginGroup> groups)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginGroup group in groups)
        {
            foreach (string system in group.Systems)
                counts[system] = counts.GetValueOrDefault(system) + 1;
        }

        return counts;
    }

    private static Dictionary<string, int> PlatformCounts(IEnumerable<PluginGroup> groups)
    {
        var counts = KnownPlatforms.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (PluginGroup group in groups)
        {
            foreach (string platform in KnownPlatforms)
            {
                if (MatchesPlatform(group, platform))
                    counts[platform]++;
            }
        }

        return counts;
    }

    private static Dictionary<string, int> RuntimeCounts(IEnumerable<PluginGroup> groups)
    {
        var counts = KnownRuntimes.ToDictionary(r => r, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (PluginGroup group in groups)
        {
            foreach (string runtime in KnownRuntimes)
            {
                if (MatchesRuntime(group, runtime))
                    counts[runtime]++;
            }
        }

        return counts;
    }
}
