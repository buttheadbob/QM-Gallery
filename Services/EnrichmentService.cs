using System.Text.Json;
using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

/// <summary>
/// Fills in pinned-commit dates, optional repository metadata, Quasar package
/// versions and READMEs. Everything is written through <see cref="GalleryStore"/>.
/// </summary>
public sealed class EnrichmentService
{
    private static readonly TimeSpan FailedCommitRetry = TimeSpan.FromDays(7);

    private readonly GalleryStore _store;
    private readonly GitHubClient _github;
    private readonly ReadmeService _readmes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly GalleryOptions _options;
    private readonly ILogger<EnrichmentService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EnrichmentService(
        GalleryStore store,
        GitHubClient github,
        ReadmeService readmes,
        IHttpClientFactory httpFactory,
        IOptions<GalleryOptions> options,
        ILogger<EnrichmentService> logger)
    {
        _store = store;
        _github = github;
        _readmes = readmes;
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            await EnrichCommitsAsync(cancellationToken);
            await EnrichReposAsync(cancellationToken);
            await EnrichQuasarVersionsAsync(cancellationToken);
            await PrefetchReadmesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnrichCommitsAsync(CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, (string Repo, string Sha)>(StringComparer.OrdinalIgnoreCase);

        foreach (PluginEntry entry in _store.View.Snapshot.Entries)
        {
            if (entry.Type != ManifestType.GitHubPlugin
                || string.IsNullOrWhiteSpace(entry.RepoKey)
                || entry.Commit.Length < 7)
            {
                continue;
            }

            string key = $"{entry.RepoKey}@{entry.Commit}".ToLowerInvariant();
            if (pending.ContainsKey(key))
                continue;

            CommitInfo? existing = _store.GetCommit(entry.RepoKey, entry.Commit);
            if (existing is not null
                && (!existing.Failed
                    || DateTimeOffset.UtcNow - existing.FetchedAt < FailedCommitRetry))
            {
                continue;
            }

            pending[key] = (entry.RepoKey, entry.Commit);
        }

        if (pending.Count == 0)
            return;

        _logger.LogInformation("Resolving {Count} pinned commits", pending.Count);
        int completed = 0;
        int flushed = 0;

        await Parallel.ForEachAsync(
            pending.Values,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.EnrichmentConcurrency),
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                CommitInfo? commit = await _github.GetCommitAsync(item.Repo, item.Sha, token);
                _store.SetCommit(commit ?? new CommitInfo
                {
                    RepoKey = item.Repo,
                    Sha = item.Sha,
                    Failed = true,
                    FetchedAt = DateTimeOffset.UtcNow
                });

                int done = Interlocked.Increment(ref completed);
                if (done - flushed >= 25)
                {
                    flushed = done;
                    await _store.FlushAsync(token);
                }
            });

        await _store.FlushAsync(cancellationToken);
    }

    private async Task EnrichReposAsync(CancellationToken cancellationToken)
    {
        if (!_github.TokenConfigured || !_github.BudgetAvailable)
            return;

        string[] repos = _store.View.Plugins
            .Select(g => g.RepoKey)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(repo =>
            {
                RepoInfo? existing = _store.GetRepo(repo);
                return existing is null
                    || DateTimeOffset.UtcNow - existing.FetchedAt > TimeSpan.FromHours(_options.RepoMetadataHours);
            })
            .ToArray();

        if (repos.Length == 0)
            return;

        _logger.LogInformation("Refreshing metadata for {Count} repositories", repos.Length);
        int completed = 0;

        await Parallel.ForEachAsync(
            repos,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.EnrichmentConcurrency),
                CancellationToken = cancellationToken
            },
            async (repo, token) =>
            {
                if (!_github.BudgetAvailable)
                    return;

                RepoInfo? info = await _github.GetRepoAsync(repo, token);
                if (info is not null)
                    _store.SetRepo(info);

                int done = Interlocked.Increment(ref completed);
                if (done % 25 == 0)
                    await _store.FlushAsync(token);
            });

        await _store.FlushAsync(cancellationToken);
    }

    private async Task EnrichQuasarVersionsAsync(CancellationToken cancellationToken)
    {
        PluginEntry[] pending = _store.View.Snapshot.Entries
            .Where(e => string.Equals(e.HubKey, "quasar", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(e.Version)
                && !string.IsNullOrWhiteSpace(e.RepoKey)
                && !string.IsNullOrWhiteSpace(e.PackageManifest)
                && e.Commit.Length >= 7)
            .ToArray();

        if (pending.Length == 0)
            return;

        HttpClient client = _httpFactory.CreateClient(HttpClients.RawContent);
        bool changed = false;

        foreach (PluginEntry entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.RepoKey) || string.IsNullOrWhiteSpace(entry.PackageManifest))
                continue;

            try
            {
                string path = string.Join(
                    '/',
                    entry.PackageManifest.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
                string url = $"https://raw.githubusercontent.com/{entry.RepoKey}/{entry.Commit}/{path}";
                string json = await client.GetStringAsync(url, cancellationToken);
                using JsonDocument document = JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty("version", out JsonElement version)
                    && version.ValueKind == JsonValueKind.String)
                {
                    string? value = version.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _store.SetVersion(entry.Id, value.Trim());
                        changed = true;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Could not read package manifest for {Plugin}: {Message}",
                    entry.FriendlyName,
                    ex.Message);
            }
        }

        if (changed)
            await _store.FlushAsync(cancellationToken);
    }

    private async Task PrefetchReadmesAsync(CancellationToken cancellationToken)
    {
        PluginGroup[] pending = _store.View.Plugins
            .Where(g => !string.IsNullOrWhiteSpace(g.RepoKey) && !string.IsNullOrWhiteSpace(g.CommitSha))
            .ToArray();

        int completed = 0;
        foreach (PluginGroup group in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _readmes.PrefetchAsync(group, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("README prefetch failed for {Plugin}: {Message}", group.Name, ex.Message);
            }

            completed++;
            if (completed % 50 == 0)
                _logger.LogInformation("README prefetch progress: {Done}/{Total}", completed, pending.Length);
        }
    }
}
