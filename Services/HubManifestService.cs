using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

public sealed class HubFetchResult
{
    public required HubDefinition Hub { get; init; }
    public List<PluginEntry> Entries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public string? Error { get; set; }
    public DateTimeOffset FetchedAt { get; } = DateTimeOffset.UtcNow;
}

public sealed class HubManifestService
{
    private const long MaxArchiveBytes = 80L * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly GalleryOptions _options;
    private readonly ILogger<HubManifestService> _logger;

    public HubManifestService(
        IHttpClientFactory httpFactory,
        IOptions<GalleryOptions> options,
        ILogger<HubManifestService> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HubFetchResult>> FetchAllAsync(CancellationToken cancellationToken)
    {
        Task<HubFetchResult>[] tasks = _options.Hubs
            .Select(hub => FetchAsync(hub, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    public async Task<HubFetchResult> FetchAsync(HubDefinition hub, CancellationToken cancellationToken)
    {
        var result = new HubFetchResult { Hub = hub };

        try
        {
            string url =
                $"https://codeload.github.com/{hub.Repo}/zip/refs/heads/{Uri.EscapeDataString(hub.Branch)}";

            HttpClient client = _httpFactory.CreateClient(HttpClients.HubArchive);
            using HttpResponseMessage response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                result.Error = $"Hub download failed ({(int)response.StatusCode} {response.StatusCode})";
                _logger.LogWarning("Hub {Hub} download failed: {Status}", hub.Key, response.StatusCode);
                return result;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(stream, buffer, MaxArchiveBytes, cancellationToken);
            buffer.Position = 0;

            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.FullName.EndsWith('/') || !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? relative = GetManifestPath(entry.FullName, hub.PluginsPath);
                if (relative is null)
                    continue;

                try
                {
                    using Stream entryStream = entry.Open();
                    using var reader = new StreamReader(entryStream);
                    string xml = await reader.ReadToEndAsync(cancellationToken);

                    PluginEntry parsed = ManifestParser.Parse(xml, hub, relative);

                    if (parsed.Type == ManifestType.ModPlugin && parsed.WorkshopId is null)
                        parsed.WorkshopId = parsed.Id;

                    result.Entries.Add(parsed);
                }
                catch (Exception ex)
                {
                    string message = $"{relative}: {ex.Message}";
                    result.Warnings.Add(message);
                    _logger.LogWarning("Could not parse {Hub}/{Path}: {Error}", hub.Key, relative, ex.Message);
                }
            }

            _logger.LogInformation(
                "Hub {Hub}: {Count} descriptors from {Repo}",
                hub.Key,
                result.Entries.Count,
                hub.Repo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "Hub {Hub} could not be refreshed", hub.Key);
        }

        return result;
    }

    private static string? GetManifestPath(string fullName, string pluginsPath)
    {
        string normalized = fullName.Replace('\\', '/');
        string marker = $"/{pluginsPath.Trim('/')}/";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        string relative = normalized[(index + 1)..];
        return relative.StartsWith(pluginsPath.Trim('/') + "/", StringComparison.OrdinalIgnoreCase)
            ? relative
            : null;
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException("Hub archive is unexpectedly large");

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }
}
