using System.Text.RegularExpressions;
using System.Xml.Linq;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

public static partial class ManifestParser
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-_.]{0,38}/[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled)]
    private static partial Regex RepoPattern();

    public static bool IsValidRepo(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains('\\')
        && RepoPattern().IsMatch(value);

    public static PluginEntry Parse(string xml, HubDefinition hub, string manifestPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Invalid XML in {manifestPath}: {ex.Message}", ex);
        }

        XElement root = document.Root
            ?? throw new InvalidDataException($"Missing root element in {manifestPath}");

        string? typeName = root.Attribute(Xsi + "type")?.Value;
        string normalizedPath = manifestPath.Replace('\\', '/');
        bool isMod = string.Equals(typeName, "ModPlugin", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("/Mods/", StringComparison.OrdinalIgnoreCase);

        var entry = new PluginEntry
        {
            HubKey = hub.Key,
            HubName = hub.Name,
            ManifestPath = normalizedPath,
            ManifestBlobUrl = BlobUrl(hub, normalizedPath),
            ManifestRawUrl = RawUrl(hub, normalizedPath),
            RawXml = xml,
            Type = isMod ? ManifestType.ModPlugin : ManifestType.GitHubPlugin,
            Id = Value(root, "Id"),
            FriendlyName = Value(root, "FriendlyName"),
            Author = Value(root, "Author"),
            Tooltip = Value(root, "Tooltip"),
            Description = Text.CleanDescription(Value(root, "Description")),
            Commit = Value(root, "Commit"),
            RepoId = NullIfEmpty(Value(root, "RepoId")),
            Hidden = Bool(root, "Hidden"),
            Recommended = Bool(root, "Recommended"),
            ImplicitLoading = Bool(root, "ImplicitLoading"),
            PluginKind = NullIfEmpty(Value(root, "PluginKind")),
            ProjectPath = NullIfEmpty(Value(root, "ProjectPath")),
            PackageManifest = NullIfEmpty(Value(root, "PackageManifest")),
            QuasarVersion = NullIfEmpty(Value(root, "QuasarVersion")),
            Platforms = Text.SplitList(Value(root, "Platforms")),
            Runtimes = Text.SplitList(Value(root, "Runtimes")),
            DependencyIds = Children(root, "DependencyIds"),
            CompanionIds = Children(root, "CompanionPluginIds"),
            SourceDirectories = Children(root, "SourceDirectories"),
        };

        if (entry.Description.Length == 0)
            entry.Description = entry.Tooltip;

        if (entry.FriendlyName.Length == 0)
        {
            entry.FriendlyName = entry.Id;
            entry.Warnings.Add("Missing FriendlyName");
        }

        if (entry.Id.Length == 0)
            entry.Warnings.Add("Missing Id");

        // Pulsar allows the plugin id itself to be the owner/repo identifier.
        string? repo = entry.RepoId;
        if (string.IsNullOrWhiteSpace(repo) && IsValidRepo(entry.Id))
            repo = entry.Id;

        if (isMod)
        {
            entry.WorkshopId = entry.Id;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(repo))
            {
                entry.Warnings.Add("No GitHub repository could be resolved");
            }
            else if (!IsValidRepo(repo))
            {
                entry.Warnings.Add($"Invalid repository identifier: {repo}");
                repo = null;
            }
            else if (string.IsNullOrWhiteSpace(entry.Commit))
            {
                entry.Warnings.Add("Missing pinned commit");
            }

            entry.RepoKey = repo;

            if (string.IsNullOrWhiteSpace(entry.Commit))
            {
                entry.Commit = "";
            }
        }

        entry.NuGetReferences = ParseNuGet(root);
        entry.Assets = ParseAssets(root);
        entry.AlternateVersions = ParseAlternateVersions(root);

        return entry;
    }

    private static List<NuGetReference> ParseNuGet(XElement root)
    {
        var result = new List<NuGetReference>();
        XElement? container = Child(root, "NuGetReferences");
        if (container is null)
            return result;

        foreach (XElement element in container.Elements())
        {
            string? name = Attribute(element, "Include") ?? Attribute(element, "Name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new NuGetReference
            {
                Name = name.Trim(),
                Version = (Attribute(element, "Version") ?? element.Value).Trim() is { Length: > 0 } version
                    ? version
                    : null
            });
        }

        return result;
    }

    private static List<PluginAsset> ParseAssets(XElement root)
    {
        var result = new List<PluginAsset>();

        foreach (XElement element in root.Elements().Where(e => e.Name.LocalName == "Asset"))
        {
            string name = Attribute(element, "Name") ?? "";
            string path = Attribute(element, "Path") ?? element.Value;
            if (string.IsNullOrWhiteSpace(path))
                continue;
            result.Add(new PluginAsset { Name = name.Trim(), Path = path.Trim() });
        }

        XElement? folder = Child(root, "AssetFolder");
        if (folder is not null && !string.IsNullOrWhiteSpace(folder.Value))
            result.Add(new PluginAsset { Name = "AssetFolder", Path = folder.Value.Trim() });

        return result;
    }

    private static List<AlternateVersion> ParseAlternateVersions(XElement root)
    {
        var result = new List<AlternateVersion>();
        XElement? container = Child(root, "AlternateVersions");
        if (container is null)
            return result;

        foreach (XElement element in container.Elements())
        {
            result.Add(new AlternateVersion
            {
                Name = Value(element, "Name"),
                Commit = Value(element, "Commit"),
                Repo = NullIfEmpty(Value(element, "Repo"))
            });
        }

        return result;
    }

    private static string BlobUrl(HubDefinition hub, string path) =>
        $"https://github.com/{hub.Repo}/blob/{hub.Branch}/{EscapePath(path)}";

    private static string RawUrl(HubDefinition hub, string path) =>
        $"https://raw.githubusercontent.com/{hub.Repo}/{hub.Branch}/{EscapePath(path)}";

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static string Value(XElement parent, string localName) =>
        Child(parent, localName)?.Value?.Trim() ?? "";

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Bool(XElement parent, string localName) =>
        bool.TryParse(Value(parent, localName), out bool result) && result;

    private static List<string> Children(XElement parent, string localName)
    {
        XElement? container = Child(parent, localName);
        if (container is null)
            return [];

        return container.Elements()
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
