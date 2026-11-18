using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

public sealed record ReadmeDocument(
    string Key,
    string RepoKey,
    string Sha,
    string FileName,
    string Markdown,
    string Html,
    DateTimeOffset FetchedAt);

public sealed class ReadmeService
{
    private static readonly string[] Candidates =
    [
        "README.md", "readme.md", "Readme.md", "README.MD",
        "README.markdown", "readme.markdown",
        "README.rst", "README.txt", "README"
    ];

    private const int MaxReadmeBytes = 512 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly StorePaths _paths;
    private readonly MarkdownRenderer _renderer;
    private readonly IMemoryCache _cache;
    private readonly GalleryOptions _options;
    private readonly ILogger<ReadmeService> _logger;
    private readonly SemaphoreSlim _fetchGate;

    public ReadmeService(
        IHttpClientFactory httpFactory,
        StorePaths paths,
        MarkdownRenderer renderer,
        IMemoryCache cache,
        IOptions<GalleryOptions> options,
        ILogger<ReadmeService> logger)
    {
        _httpFactory = httpFactory;
        _paths = paths;
        _renderer = renderer;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _fetchGate = new SemaphoreSlim(Math.Max(1, _options.ReadmeConcurrency));
    }

    public async Task<ReadmeDocument?> GetAsync(
        PluginGroup group,
        CancellationToken cancellationToken,
        bool allowFetch = true)
    {
        if (string.IsNullOrWhiteSpace(group.RepoKey) || string.IsNullOrWhiteSpace(group.CommitSha))
            return null;

        string key = Key(group.RepoKey, group.CommitSha);
        if (_cache.TryGetValue(key, out ReadmeDocument? cached) && cached is not null)
            return cached;

        string file = _paths.ReadmeFile(key);
        if (File.Exists(file))
        {
            try
            {
                string markdown = await File.ReadAllTextAsync(file, cancellationToken);
                ReadmeDocument document = Build(key, group.RepoKey, group.CommitSha, "README.md", markdown);
                Cache(document);
                return document;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not read cached README {Key}: {Message}", key, ex.Message);
            }
        }

        if (!allowFetch || WasMissingRecently(key))
            return null;

        await _fetchGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            if (File.Exists(file))
            {
                string markdown = await File.ReadAllTextAsync(file, cancellationToken);
                return Cache(Build(key, group.RepoKey, group.CommitSha, "README.md", markdown));
            }

            return await FetchAsync(key, group.RepoKey, group.CommitSha, cancellationToken);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    public async Task<bool> PrefetchAsync(PluginGroup group, CancellationToken cancellationToken)
    {
        ReadmeDocument? document = await GetAsync(group, cancellationToken, allowFetch: true);
        return document is not null;
    }

    public string? TryReadCached(PluginGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.RepoKey) || string.IsNullOrWhiteSpace(group.CommitSha))
            return null;

        string key = Key(group.RepoKey, group.CommitSha);
        return _cache.TryGetValue(key, out ReadmeDocument? cached) && cached is not null
            ? cached.Html
            : null;
    }

    private async Task<ReadmeDocument?> FetchAsync(
        string key,
        string repoKey,
        string sha,
        CancellationToken cancellationToken)
    {
        HttpClient client = _httpFactory.CreateClient(HttpClients.RawContent);

        foreach (string candidate in Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string url = $"https://raw.githubusercontent.com/{repoKey}/{sha}/{candidate}";
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("README request for {Repo} returned {Status}", repoKey, response.StatusCode);
                    return null;
                }

                if (response.Content.Headers.ContentLength > MaxReadmeBytes)
                    continue;

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > MaxReadmeBytes)
                        return null;
                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                }

                string markdown = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
                await File.WriteAllTextAsync(_paths.ReadmeFile(key), markdown, cancellationToken);

                ReadmeDocument document = Build(key, repoKey, sha, candidate, markdown);
                Cache(document);
                _logger.LogDebug("Cached README {Repo}@{Sha}", repoKey, sha[..Math.Min(8, sha.Length)]);
                return document;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("README fetch failed for {Repo}: {Message}", repoKey, ex.Message);
                return null;
            }
        }

        await MarkMissingAsync(key);
        return null;
    }

    private ReadmeDocument Build(string key, string repoKey, string sha, string fileName, string markdown)
    {
        string html = _renderer.Render(markdown, repoKey, sha);
        return new ReadmeDocument(key, repoKey, sha, fileName, markdown, html, DateTimeOffset.UtcNow);
    }

    private ReadmeDocument Cache(ReadmeDocument document)
    {
        _cache.Set(
            document.Key,
            document,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(6),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
            });
        return document;
    }

    private bool WasMissingRecently(string key)
    {
        string path = _paths.MissingFile(key);
        if (!File.Exists(path))
            return false;

        try
        {
            DateTime write = File.GetLastWriteTimeUtc(path);
            return DateTime.UtcNow - write < TimeSpan.FromHours(_options.ReadmeRetryHours);
        }
        catch
        {
            return false;
        }
    }

    private async Task MarkMissingAsync(string key)
    {
        try
        {
            string json = JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.UtcNow });
            await File.WriteAllTextAsync(_paths.MissingFile(key), json);
        }
        catch
        {
            // A missing marker is only an optimisation.
        }
    }

    private static string Key(string repoKey, string sha) =>
        Text.ShortHash($"{repoKey}@{sha}".ToLowerInvariant(), 24);
}
