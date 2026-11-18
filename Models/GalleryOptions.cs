namespace QM_Gallery.Models;

public sealed class GalleryOptions
{
    public const string SectionName = "Gallery";

    public string CacheDirectory { get; set; } = "App_Data/cache";
    public int ManifestRefreshMinutes { get; set; } = 30;
    public int RepoMetadataHours { get; set; } = 24;
    public int StatsHours { get; set; } = 6;
    public int ReadmeRetryHours { get; set; } = 168;
    public int EnrichmentConcurrency { get; set; } = 6;
    public int ReadmeConcurrency { get; set; } = 3;
    public string? GitHubToken { get; set; }
    public string? GitHubTokenFile { get; set; }
    public string? PublicBaseUrl { get; set; }
    public string DiscordUrl { get; set; } = "https://discord.gg/pulsar-1378756728107040829";
    public List<HubDefinition> Hubs { get; set; } = [];
}

public sealed class HubDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string PluginsPath { get; set; } = "Plugins";
    public string? StatsUrl { get; set; }
    public string? Homepage { get; set; }

    /// <summary>pulsar, magnetar or quasar. Drives badges and install text.</summary>
    public string Tone { get; set; } = "pulsar";

    public bool IsClient { get; set; }
    public bool IsServer { get; set; }
    public bool IsUi { get; set; }
}
