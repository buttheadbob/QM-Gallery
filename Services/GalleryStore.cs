using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

/// <summary>
/// Owns the in-memory catalog, its disk snapshot and all enrichment writes.
/// Reads are lock-free: the immutable <see cref="GalleryView"/> reference is swapped atomically.
/// </summary>
public sealed class GalleryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly GalleryOptions _options;
    private readonly StorePaths _paths;
    private readonly HubManifestService _hubs;
    private readonly StatsClient _stats;
    private readonly GitHubClient _github;
    private readonly ILogger<GalleryStore> _logger;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private GallerySnapshot _snapshot = new();
    private GalleryView _view = GalleryView.Empty;
    private bool _loaded;
    private int _revision;

    public GalleryStore(
        IOptions<GalleryOptions> options,
        StorePaths paths,
        HubManifestService hubs,
        StatsClient stats,
        GitHubClient github,
        ILogger<GalleryStore> logger)
    {
        _options = options.Value;
        _paths = paths;
        _hubs = hubs;
        _stats = stats;
        _github = github;
        _logger = logger;
    }

    public GalleryView View => Volatile.Read(ref _view);
    public GalleryOptions Options => _options;
    public StorePaths Paths => _paths;

    public event Action? Changed;

    public bool NeedsManifestRefresh =>
        _snapshot.LastManifestRefresh is null
        || DateTimeOffset.UtcNow - _snapshot.LastManifestRefresh
            > TimeSpan.FromMinutes(_options.ManifestRefreshMinutes);

    public bool NeedsStatsRefresh =>
        _snapshot.LastStatsRefresh is null
        || DateTimeOffset.UtcNow - _snapshot.LastStatsRefresh
            > TimeSpan.FromHours(_options.StatsHours);

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (View.Plugins.Count > 0 || View.Mods.Count > 0)
            return;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded)
                LoadFromDisk();

            if (View.Plugins.Count == 0 && View.Mods.Count == 0)
                await RefreshManifestsCoreAsync(cancellationToken);

            if (_snapshot.LastStatsRefresh is null
                && _options.Hubs.Any(h => !string.IsNullOrWhiteSpace(h.StatsUrl)))
            {
                await RefreshStatsAsync(cancellationToken);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task RefreshManifestsAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded)
                LoadFromDisk();
            await RefreshManifestsCoreAsync(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task RefreshStatsAsync(CancellationToken cancellationToken)
    {
        var fetched = new Dictionary<string, Dictionary<string, StatsEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (HubDefinition hub in _options.Hubs.Where(h => !string.IsNullOrWhiteSpace(h.StatsUrl)))
        {
            Dictionary<string, StatsEntry> stats = await _stats.FetchAsync(hub.StatsUrl, cancellationToken);
            if (stats.Count > 0)
                fetched[hub.Key] = stats;
        }

        if (fetched.Count == 0)
            return;

        lock (_sync)
        {
            if (fetched.TryGetValue("pulsar", out Dictionary<string, StatsEntry>? pulsar))
                _snapshot.PulsarStats = pulsar;
            if (fetched.TryGetValue("magnetar", out Dictionary<string, StatsEntry>? magnetar))
                _snapshot.MagnetarStats = magnetar;

            _snapshot.LastStatsRefresh = DateTimeOffset.UtcNow;
        }

        RebuildAndNotify();
        await SaveAsync(cancellationToken);
    }

    public void SetCommit(CommitInfo commit)
    {
        string key = CommitKey(commit.RepoKey, commit.Sha);
        lock (_sync)
            _snapshot.Commits[key] = commit;
    }

    public void SetRepo(RepoInfo repo)
    {
        lock (_sync)
            _snapshot.Repos[repo.RepoKey.ToLowerInvariant()] = repo;
    }

    public void SetVersion(string entryId, string version)
    {
        lock (_sync)
        {
            foreach (PluginEntry entry in _snapshot.Entries.Where(e =>
                         string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase)))
            {
                entry.Version = version;
            }
        }
    }

    public CommitInfo? GetCommit(string? repoKey, string? sha)
    {
        if (string.IsNullOrWhiteSpace(repoKey) || string.IsNullOrWhiteSpace(sha))
            return null;

        lock (_sync)
            return _snapshot.Commits.TryGetValue(CommitKey(repoKey, sha), out CommitInfo? commit)
                ? commit
                : null;
    }

    public RepoInfo? GetRepo(string? repoKey)
    {
        if (string.IsNullOrWhiteSpace(repoKey))
            return null;

        lock (_sync)
            return _snapshot.Repos.TryGetValue(repoKey.ToLowerInvariant(), out RepoInfo? repo)
                ? repo
                : null;
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        RebuildAndNotify();
        await SaveAsync(cancellationToken);
    }

    public void RebuildAndNotify()
    {
        GalleryView view;
        lock (_sync)
        {
            var (groups, mods) = PluginIdentity.Build(_snapshot.Entries);
            ApplyEnrichment(groups, mods);
            view = BuildView(groups, mods);
        }

        Volatile.Write(ref _view, view);
        Changed?.Invoke();
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            string json;
            lock (_sync)
            {
                json = JsonSerializer.Serialize(_snapshot, JsonOptions);
            }

            string temp = _paths.SnapshotFile + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, _paths.SnapshotFile, overwrite: true);
            _logger.LogDebug("Catalog snapshot saved ({Bytes} bytes)", json.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not save the catalog snapshot");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task RefreshManifestsCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HubFetchResult> results = await _hubs.FetchAllAsync(cancellationToken);
        var statuses = new List<HubStatus>();
        var warnings = new List<string>();
        List<PluginEntry> existingEntries = _snapshot.Entries;

        // Resolve hub HEADs for the status page when a token is available.
        foreach (HubFetchResult result in results)
        {
            string? head = await _github.GetDefaultBranchHeadAsync(
                result.Hub.Repo,
                result.Hub.Branch,
                cancellationToken);

            IEnumerable<PluginEntry> counted = result.Error is null
                ? result.Entries
                : existingEntries.Where(e =>
                    string.Equals(e.HubKey, result.Hub.Key, StringComparison.OrdinalIgnoreCase));

            statuses.Add(new HubStatus
            {
                Key = result.Hub.Key,
                Name = result.Hub.Name,
                Repo = result.Hub.Repo,
                Branch = result.Hub.Branch,
                CommitSha = head,
                FetchedAt = result.FetchedAt,
                PluginCount = counted.Count(e => e.Type == ManifestType.GitHubPlugin),
                ModCount = counted.Count(e => e.Type == ManifestType.ModPlugin),
                Error = result.Error
            });

            warnings.AddRange(result.Warnings);
        }

        lock (_sync)
        {
            var entries = new List<PluginEntry>(_snapshot.Entries);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HubFetchResult result in results)
            {
                if (result.Error is not null)
                {
                    _logger.LogWarning(
                        "Keeping cached descriptors for {Hub} after failed refresh: {Error}",
                        result.Hub.Key,
                        result.Error);
                    continue;
                }

                entries.RemoveAll(e =>
                    string.Equals(e.HubKey, result.Hub.Key, StringComparison.OrdinalIgnoreCase));
                entries.AddRange(result.Entries);
                touched.Add(result.Hub.Key);
            }

            if (touched.Count > 0)
                _snapshot.LastManifestRefresh = DateTimeOffset.UtcNow;

            _snapshot.Entries = entries;
            _snapshot.Hubs = statuses;
            _snapshot.Warnings = warnings.Distinct().Take(100).ToList();
        }

        RebuildAndNotify();
        await SaveAsync(cancellationToken);
    }

    private void ApplyEnrichment(List<PluginGroup> groups, List<ModEntry> mods)
    {
        foreach (PluginGroup group in groups)
        {
            List<PluginEntry> ordered = group.Entries
                .OrderBy(e => e.HubKey.Equals("pulsar", StringComparison.OrdinalIgnoreCase) ? 0
                    : e.HubKey.Equals("magnetar", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ToList();

            PluginEntry? commitEntry = null;
            CommitInfo? commit = null;

            foreach (PluginEntry entry in ordered)
            {
                if (string.IsNullOrWhiteSpace(entry.RepoKey) || entry.Commit.Length < 7)
                    continue;

                if (_snapshot.Commits.TryGetValue(CommitKey(entry.RepoKey, entry.Commit), out CommitInfo? candidate)
                    && candidate.Date is not null)
                {
                    commitEntry = entry;
                    commit = candidate;
                    break;
                }
            }

            if (commit is null)
            {
                PluginEntry? primary = ordered.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(e.RepoKey) && e.Commit.Length >= 7);

                if (primary is not null
                    && _snapshot.Commits.TryGetValue(CommitKey(primary.RepoKey!, primary.Commit), out CommitInfo? fallback))
                {
                    commitEntry = primary;
                    commit = fallback;
                }
            }

            PluginEntry? repoEntry = commitEntry
                ?? ordered.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.RepoKey));

            if (repoEntry?.RepoKey is string repoKey)
            {
                string normalizedRepo = repoKey.ToLowerInvariant();
                if (_snapshot.Repos.TryGetValue(normalizedRepo, out RepoInfo? repo))
                    group.Repo = repo;
            }

            if (commit is not null && commitEntry is not null)
            {
                group.CommitSha = commitEntry.Commit;
                group.CommitMessage = commit.Message;
                group.Updated = commit.Date;
                group.CommitUrl = $"https://github.com/{commit.RepoKey}/commit/{commit.Sha}";
            }

            group.PulsarStats = PickStats(group, "pulsar", _snapshot.PulsarStats);
            group.MagnetarStats = PickStats(group, "magnetar", _snapshot.MagnetarStats);
        }

        foreach (ModEntry mod in mods)
        {
            if (_snapshot.PulsarStats.TryGetValue(mod.Id.ToLowerInvariant(), out StatsEntry? stats))
                mod.Stats = stats;
        }
    }

    private static StatsEntry? PickStats(
        PluginGroup group,
        string hubKey,
        Dictionary<string, StatsEntry> stats)
    {
        if (stats.Count == 0)
            return null;

        StatsEntry? best = null;
        int bestScore = -1;

        foreach (PluginEntry entry in group.Entries)
        {
            if (!string.Equals(entry.HubKey, hubKey, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string key in CandidateKeys(entry))
            {
                if (!stats.TryGetValue(key, out StatsEntry? candidate))
                    continue;

                int score = candidate.Votes * 10 + candidate.Players;
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    private static IEnumerable<string> CandidateKeys(PluginEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Id))
            yield return entry.Id.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(entry.RepoKey))
            yield return entry.RepoKey.ToLowerInvariant();
    }

    private GalleryView BuildView(List<PluginGroup> groups, List<ModEntry> mods)
    {
        var bySlug = new Dictionary<string, PluginGroup>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, PluginGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (PluginGroup group in groups)
        {
            bySlug[group.Slug] = group;

            foreach (PluginEntry entry in group.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    byId.TryAdd(entry.Id, group);
            }
        }

        _revision++;
        return new GalleryView(
            _snapshot,
            groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            mods,
            bySlug,
            byId,
            _revision,
            DateTimeOffset.UtcNow);
    }

    private void LoadFromDisk()
    {
        _loaded = true;

        try
        {
            if (!File.Exists(_paths.SnapshotFile))
            {
                _logger.LogInformation("No catalog snapshot found; the catalog will be built from the hubs");
                return;
            }

            string json = File.ReadAllText(_paths.SnapshotFile);
            GallerySnapshot? snapshot = JsonSerializer.Deserialize<GallerySnapshot>(json, JsonOptions);

            if (snapshot is null || snapshot.SchemaVersion != 1)
            {
                _logger.LogWarning("Ignoring an incompatible catalog snapshot");
                return;
            }

            lock (_sync)
                _snapshot = snapshot;

            RebuildAndNotify();
            _logger.LogInformation(
                "Loaded catalog snapshot: {Plugins} plugins, {Mods} mods",
                View.Plugins.Count,
                View.Mods.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the catalog snapshot; it will be rebuilt");
        }
    }

    private static string CommitKey(string repoKey, string sha) =>
        $"{repoKey.ToLowerInvariant()}@{sha.ToLowerInvariant()}";
}
