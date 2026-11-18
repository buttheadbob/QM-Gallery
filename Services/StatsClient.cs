using System.Text.Json;
using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

public sealed class StatsClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly GalleryOptions _options;
    private readonly ILogger<StatsClient> _logger;

    public StatsClient(
        IHttpClientFactory httpFactory,
        IOptions<GalleryOptions> options,
        ILogger<StatsClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Dictionary<string, StatsEntry>> FetchAsync(
        string? url,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, StatsEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(url))
            return result;

        try
        {
            HttpClient client = _httpFactory.CreateClient(HttpClients.Stats);
            await using Stream stream = await client.GetStreamAsync(url, cancellationToken);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!json.RootElement.TryGetProperty("Stats", out JsonElement stats)
                || stats.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (JsonProperty property in stats.EnumerateObject())
            {
                JsonElement value = property.Value;
                if (value.ValueKind != JsonValueKind.Object)
                    continue;

                result[property.Name.ToLowerInvariant()] = new StatsEntry
                {
                    Players = Int(value, "Players"),
                    Upvotes = Int(value, "Upvotes"),
                    Downvotes = Int(value, "Downvotes"),
                    Rating = Int(value, "Rating"),
                    Tried = Bool(value, "Tried"),
                    Vote = Int(value, "Vote")
                };
            }

            _logger.LogInformation("Loaded {Count} statistics from {Url}", result.Count, url);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load statistics from {Url}: {Message}", url, ex.Message);
        }

        return result;
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
