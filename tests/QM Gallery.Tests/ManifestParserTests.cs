using QM_Gallery.Models;
using QM_Gallery.Services;

namespace QM_Gallery.Tests;

public sealed class ManifestParserTests
{
    private static HubDefinition Hub() => new()
    {
        Key = "quasar",
        Name = "Quasar",
        Repo = "CometWorks/quasar-hub",
        Branch = "main"
    };

    [Fact]
    public void ParsesGitHubDescriptor()
    {
        const string xml = """
            <?xml version="1.0"?>
            <PluginData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">
              <Id>ac6149e7-8a0d-4dca-b470-7de4bed50e1f</Id>
              <RepoId>OwendB1/quasar-koth</RepoId>
              <FriendlyName>King of the Hill</FriendlyName>
              <Author>OwendB1</Author>
              <Tooltip>Live KOTH scores</Tooltip>
              <Description>Line one.
                 Line two.</Description>
              <PluginKind>QuasarUiPlugin</PluginKind>
              <QuasarVersion>&gt;=1.6.0.54</QuasarVersion>
              <CompanionPluginIds>
                <CompanionPluginId>owendb.koth</CompanionPluginId>
              </CompanionPluginIds>
              <Platforms>Windows,Linux</Platforms>
              <Hidden>false</Hidden>
              <Commit>6236411de26eff4fd01c6ca7b4a889e617077bc2</Commit>
            </PluginData>
            """;

        PluginEntry entry = ManifestParser.Parse(xml, Hub(), "Plugins/KingOfTheHill.xml");

        Assert.Equal(ManifestType.GitHubPlugin, entry.Type);
        Assert.Equal("King of the Hill", entry.FriendlyName);
        Assert.Equal("OwendB1/quasar-koth", entry.RepoKey);
        Assert.Equal("6236411de26eff4fd01c6ca7b4a889e617077bc2", entry.Commit);
        Assert.Equal(["Windows", "Linux"], entry.Platforms);
        Assert.Equal(["owendb.koth"], entry.CompanionIds);
        Assert.Equal("Line one.\nLine two.".ReplaceLineEndings("\n"), entry.Description.ReplaceLineEndings("\n"));
        Assert.False(entry.Hidden);
        Assert.Empty(entry.Warnings);
    }

    [Fact]
    public void UsesIdAsRepositoryForLegacyPulsarDescriptors()
    {
        const string xml = """
            <?xml version="1.0"?>
            <PluginData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">
              <Id>WesternGamer/AddMissingSearchBoxes</Id>
              <FriendlyName>Add Missing Search Boxes</FriendlyName>
              <Author>WesternGamer</Author>
              <Commit>abc1234</Commit>
            </PluginData>
            """;

        PluginEntry entry = ManifestParser.Parse(xml, Hub(), "Plugins/AddMissingSearchBoxes.xml");

        Assert.Equal("WesternGamer/AddMissingSearchBoxes", entry.RepoKey);
        Assert.True(ManifestParser.IsValidRepo(entry.RepoKey));
        Assert.Empty(entry.Warnings);
    }

    [Fact]
    public void DetectsWorkshopMods()
    {
        const string xml = """
            <?xml version="1.0"?>
            <PluginData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="ModPlugin">
              <Id>801185519</Id>
              <FriendlyName>Colorful Icons</FriendlyName>
              <Author>Someone</Author>
            </PluginData>
            """;

        PluginEntry entry = ManifestParser.Parse(xml, Hub(), "Plugins/Mods/ColorfulIcons.xml");

        Assert.Equal(ManifestType.ModPlugin, entry.Type);
        Assert.Equal("801185519", entry.WorkshopId);
        Assert.Null(entry.RepoKey);
    }

    [Fact]
    public void RejectsRepositoryIdentifiersThatCouldRedirectCredentials()
    {
        const string xml = """
            <?xml version="1.0"?>
            <PluginData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">
              <Id>12345678-1234-1234-1234-123456789012</Id>
              <RepoId>https://evil.example/owner/repo</RepoId>
              <FriendlyName>Bad</FriendlyName>
              <Author>Someone</Author>
              <Commit>abc1234</Commit>
            </PluginData>
            """;

        PluginEntry entry = ManifestParser.Parse(xml, Hub(), "Plugins/Bad.xml");

        Assert.Null(entry.RepoKey);
        Assert.Contains(entry.Warnings, warning => warning.Contains("Invalid repository"));
    }
}
