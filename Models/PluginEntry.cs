namespace QM_Gallery.Models;

public enum ManifestType
{
    GitHubPlugin,
    ModPlugin
}

/// <summary>One XML descriptor exactly as it exists in a hub repository.</summary>
public sealed class PluginEntry
{
    public string HubKey { get; set; } = "";
    public string HubName { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestBlobUrl { get; set; } = "";
    public string ManifestRawUrl { get; set; } = "";
    public string RawXml { get; set; } = "";
    public ManifestType Type { get; set; } = ManifestType.GitHubPlugin;

    public string Id { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Author { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Description { get; set; } = "";
    public string Commit { get; set; } = "";
    public string? RepoId { get; set; }
    public string? RepoKey { get; set; }

    public bool Hidden { get; set; }
    public bool Recommended { get; set; }
    public bool ImplicitLoading { get; set; }

    public string? PluginKind { get; set; }
    public string? ProjectPath { get; set; }
    public string? PackageManifest { get; set; }
    public string? QuasarVersion { get; set; }
    public string? Version { get; set; }

    public List<string> Platforms { get; set; } = [];
    public List<string> Runtimes { get; set; } = [];
    public List<string> DependencyIds { get; set; } = [];
    public List<string> CompanionIds { get; set; } = [];
    public List<string> SourceDirectories { get; set; } = [];
    public List<NuGetReference> NuGetReferences { get; set; } = [];
    public List<PluginAsset> Assets { get; set; } = [];
    public List<AlternateVersion> AlternateVersions { get; set; } = [];

    public string? WorkshopId { get; set; }
    public List<string> Warnings { get; set; } = [];

    public string? RepoOwner =>
        string.IsNullOrWhiteSpace(RepoKey) || !RepoKey.Contains('/')
            ? null
            : RepoKey.Split('/', 2)[0];
}

public sealed class NuGetReference
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class PluginAsset
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class AlternateVersion
{
    public string Name { get; set; } = "";
    public string Commit { get; set; } = "";
    public string? Repo { get; set; }
}
