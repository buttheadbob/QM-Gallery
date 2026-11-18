using QM_Gallery.Models;

namespace QM_Gallery.Services;

/// <summary>
/// Merges descriptors that describe the same plugin: same id across hubs, or
/// same repository and display name (legacy id aliases).
/// </summary>
public static class PluginIdentity
{
    private static readonly string[] HubPriority = ["pulsar", "magnetar", "quasar"];

    public static (List<PluginGroup> Groups, List<ModEntry> Mods) Build(IEnumerable<PluginEntry> entries)
    {
        PluginEntry[] all = entries.ToArray();
        PluginEntry[] plugins = all.Where(e => e.Type == ManifestType.GitHubPlugin).ToArray();
        PluginEntry[] mods = all.Where(e => e.Type == ManifestType.ModPlugin).ToArray();

        var groups = BuildGroups(plugins);
        AttachDependencyFlags(groups);
        var modEntries = BuildMods(mods);

        return (groups, modEntries);
    }

    private static List<PluginGroup> BuildGroups(PluginEntry[] entries)
    {
        if (entries.Length == 0)
            return [];

        int[] parent = Enumerable.Range(0, entries.Length).ToArray();

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB)
                parent[rootB] = rootA;
        }

        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byRepoName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entries.Length; i++)
        {
            PluginEntry entry = entries[i];

            string id = entry.Id.Trim();
            if (id.Length > 0)
            {
                if (byId.TryGetValue(id, out int existing))
                    Union(existing, i);
                else
                    byId[id] = i;
            }

            if (!string.IsNullOrWhiteSpace(entry.RepoKey))
            {
                string key = $"{entry.RepoKey}|{Text.NormalizeName(entry.FriendlyName)}";
                if (byRepoName.TryGetValue(key, out int existing))
                    Union(existing, i);
                else
                    byRepoName[key] = i;
            }
        }

        var buckets = entries
            .Select((entry, index) => (entry, root: Find(index)))
            .GroupBy(x => x.root)
            .Select(g => g.Select(x => x.entry).ToList())
            .ToList();

        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<PluginGroup>(buckets.Count);

        foreach (List<PluginEntry> bucket in buckets)
        {
            PluginEntry primary = bucket
                .OrderBy(e => HubRank(e.HubKey))
                .ThenByDescending(e => e.FriendlyName.Length)
                .First();

            string name = FirstNonEmpty([primary.FriendlyName, .. bucket.Select(e => e.FriendlyName)]) ?? "Unnamed plugin";
            string author = FirstNonEmpty([primary.Author, .. bucket.Select(e => e.Author)]) ?? "Unknown";
            string tooltip = FirstNonEmpty([primary.Tooltip, .. bucket.Select(e => e.Tooltip)]) ?? "";
            string description = primary.Description.Length > 0
                ? primary.Description
                : FirstNonEmpty(bucket.Select(e => e.Description)) ?? tooltip;

            string? repoKey = primary.RepoKey
                ?? bucket.Select(e => e.RepoKey).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

            string canonical = repoKey ?? primary.Id;
            string slug = Text.Kebab(name);
            if (!usedSlugs.Add(slug))
            {
                slug = $"{slug}-{Text.ShortHash(canonical, 6)}";
                int suffix = 2;
                while (!usedSlugs.Add(slug))
                    slug = $"{Text.Kebab(name)}-{Text.ShortHash(canonical, 6)}-{suffix++}";
            }

            List<string> ids = bucket
                .Select(e => e.Id.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var group = new PluginGroup
            {
                Slug = slug,
                Name = name.Trim(),
                Author = author.Trim(),
                Tooltip = Text.OneLine(tooltip),
                Description = description,
                RepoKey = repoKey,
                RepoOwner = repoKey?.Split('/', 2).FirstOrDefault(),
                Version = primary.Version ?? FirstNonEmpty(bucket.Select(e => e.Version)),
                Entries = bucket,
                Systems = bucket
                    .Select(e => e.HubKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(HubRank)
                    .ToList(),
                Aliases = ids.Where(id =>
                        !string.Equals(id, primary.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                Platforms = bucket
                    .SelectMany(e => e.Platforms)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Runtimes = bucket
                    .SelectMany(e => e.Runtimes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DependencyIds = bucket
                    .SelectMany(e => e.DependencyIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                CompanionIds = bucket
                    .SelectMany(e => e.CompanionIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                IsHidden = bucket.All(e => e.Hidden),
                Recommended = bucket.Any(e => e.Recommended),
                IsLegacyAlias = ids.Count > 1,
                PrimaryId = primary.Id,
                CommitSha = primary.Commit,
                CommitUrl = repoKey is not null && primary.Commit.Length >= 7
                    ? $"https://github.com/{repoKey}/commit/{primary.Commit}"
                    : null
            };

            group.SearchText = string.Join(
                ' ',
                new[]
                {
                    group.Name,
                    group.Author,
                    group.Tooltip,
                    group.Description,
                    group.RepoKey ?? "",
                    string.Join(' ', group.Aliases),
                    group.PrimaryId ?? ""
                }).ToLowerInvariant();

            groups.Add(group);
        }

        return groups
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ModEntry> BuildMods(PluginEntry[] entries)
    {
        return entries
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Id) ? e.ManifestPath : e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                PluginEntry primary = group
                    .OrderByDescending(e => e.Description.Length)
                    .First();

                var mod = new ModEntry
                {
                    Id = primary.Id,
                    Name = string.IsNullOrWhiteSpace(primary.FriendlyName) ? primary.Id : primary.FriendlyName,
                    Author = string.IsNullOrWhiteSpace(primary.Author) ? "Unknown" : primary.Author,
                    Tooltip = Text.OneLine(primary.Tooltip),
                    Description = primary.Description,
                    Hidden = group.All(e => e.Hidden),
                    HubKey = primary.HubKey,
                    HubName = primary.HubName,
                    ManifestPath = primary.ManifestPath,
                    ManifestBlobUrl = primary.ManifestBlobUrl,
                    ManifestRawUrl = primary.ManifestRawUrl,
                    RawXml = primary.RawXml
                };

                mod.SearchText = string.Join(
                    ' ',
                    mod.Name,
                    mod.Author,
                    mod.Tooltip,
                    mod.Description,
                    mod.Id).ToLowerInvariant();

                return mod;
            })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AttachDependencyFlags(List<PluginGroup> groups)
    {
        var byId = new Dictionary<string, PluginGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginGroup group in groups)
        {
            foreach (PluginEntry entry in group.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    byId.TryAdd(entry.Id, group);
            }
        }

        var referenced = new HashSet<PluginGroup>();

        foreach (PluginGroup group in groups)
        {
            foreach (string id in group.DependencyIds.Concat(group.CompanionIds))
            {
                if (byId.TryGetValue(id, out PluginGroup? target) && !ReferenceEquals(target, group))
                    referenced.Add(target);
            }
        }

        foreach (PluginGroup group in groups)
            group.IsDependency = group.IsHidden && referenced.Contains(group);
    }

    private static int HubRank(string key)
    {
        int index = Array.FindIndex(HubPriority, h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? HubPriority.Length : index;
    }

    private static string? FirstNonEmpty(IEnumerable<string?> values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
