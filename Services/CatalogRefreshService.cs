using QM_Gallery.Models;

namespace QM_Gallery.Services;

public sealed class CatalogRefreshService : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);

    private readonly GalleryStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly ILogger<CatalogRefreshService> _logger;

    public CatalogRefreshService(
        GalleryStore store,
        EnrichmentService enrichment,
        ILogger<CatalogRefreshService> logger)
    {
        _store = store;
        _enrichment = enrichment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.EnsureReadyAsync(stoppingToken);

                if (_store.NeedsStatsRefresh && _store.View.Plugins.Count > 0)
                    await _store.RefreshStatsAsync(stoppingToken);

                if (_store.NeedsManifestRefresh && _store.View.Plugins.Count > 0)
                    await _store.RefreshManifestsAsync(stoppingToken);

                await _store.EnsureReadyAsync(stoppingToken);
                await _enrichment.RunAsync(stoppingToken);

                if (_store.View.Snapshot.LastStatsRefresh is null)
                    await _store.RefreshStatsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background catalog refresh failed; retrying in five minutes");
            }

            try
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
