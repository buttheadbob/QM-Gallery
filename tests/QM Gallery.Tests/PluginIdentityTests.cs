using QM_Gallery.Models;
using QM_Gallery.Services;

namespace QM_Gallery.Tests;

public sealed class PluginIdentityTests
{
    private static PluginEntry Entry(
        string hub,
        string id,
        string? repo,
        string name,
        bool hidden = false,
        IEnumerable<string>? dependencies = null,
        IEnumerable<string>? companions = null) => new()
    {
        HubKey = hub,
        HubName = hub,
        Id = id,
        RepoKey = repo,
        FriendlyName = name,
        Author = "Author",
        Commit = "abcdef1234",
        Hidden = hidden,
        DependencyIds = dependencies?.ToList() ?? [],
        CompanionIds = companions?.ToList() ?? []
    };

    [Fact]
    public void MergesSameIdAcrossHubsAndKeepsTheClientName()
    {
        PluginEntry pulsar = Entry("pulsar", "4BD38E0B", "OwendB1/se-unified-storage", "Unified Storage");
        PluginEntry magnetar = Entry("magnetar", "4BD38E0B", "OwendB1/se-unified-storage", "Unified Storage Server Companion");

        var (groups, _) = PluginIdentity.Build([pulsar, magnetar]);

        PluginGroup group = Assert.Single(groups);
        Assert.Equal("Unified Storage", group.Name);
        Assert.Equal(["pulsar", "magnetar"], group.Systems);
        Assert.Equal("OwendB1/se-unified-storage", group.RepoKey);
    }

    [Fact]
    public void MergesLegacyAliasesWithTheSameRepositoryAndName()
    {
        PluginEntry current = Entry("magnetar", "dotnet-compat", "CometWorks/dotnet-compat", "DotNetCompat");
        PluginEntry legacy = Entry("magnetar", "se-dotnet-compat", "CometWorks/dotnet-compat", "DotNetCompat");

        var (groups, _) = PluginIdentity.Build([current, legacy]);

        PluginGroup group = Assert.Single(groups);
        Assert.Equal("dotnet-compat", group.PrimaryId);
        Assert.Contains("se-dotnet-compat", group.Aliases);
        Assert.True(group.IsLegacyAlias);
    }

    [Fact]
    public void KeepsDifferentPluginsFromTheSameMonorepoSeparate()
    {
        PluginEntry first = Entry("pulsar", "one", "scotlandsyard/SpaceEngineersPlugins", "Polar's ADS Lean");
        PluginEntry second = Entry("pulsar", "two", "scotlandsyard/SpaceEngineersPlugins", "Polar's Grid Mover");

        var (groups, _) = PluginIdentity.Build([first, second]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void APluginHiddenInEveryHubIsHidden()
    {
        PluginEntry pulsar = Entry("pulsar", "x", "Owner/Repo", "Hidden Plugin", hidden: true);
        PluginEntry magnetar = Entry("magnetar", "x", "Owner/Repo", "Hidden Plugin", hidden: true);

        var (groups, _) = PluginIdentity.Build([pulsar, magnetar]);

        Assert.True(groups[0].IsHidden);
    }

    [Fact]
    public void AVisibleEditionOfTheSamePluginKeepsTheGroupVisible()
    {
        PluginEntry hidden = Entry("magnetar", "x", "Owner/Repo", "Plugin", hidden: true);
        PluginEntry visible = Entry("pulsar", "x", "Owner/Repo", "Plugin");

        var (groups, _) = PluginIdentity.Build([hidden, visible]);

        Assert.False(groups[0].IsHidden);
    }

    [Fact]
    public void MarksReferencedHiddenPluginsAsDependencies()
    {
        PluginEntry dependency = Entry("pulsar", "dep", "Owner/Dependency", "Dependency", hidden: true);
        PluginEntry consumer = Entry("pulsar", "main", "Owner/Main", "Main", dependencies: ["dep"]);

        var (groups, _) = PluginIdentity.Build([dependency, consumer]);

        PluginGroup dep = groups.Single(g => g.Name == "Dependency");
        Assert.True(dep.IsDependency);
    }

    [Fact]
    public void KeepsWorkshopModsOutOfThePluginGroups()
    {
        PluginEntry plugin = Entry("pulsar", "plugin", "Owner/Repo", "Plugin");
        PluginEntry mod = Entry("pulsar", "801185519", null, "Colorful Icons");
        mod.Type = ManifestType.ModPlugin;

        var (groups, mods) = PluginIdentity.Build([plugin, mod]);

        Assert.Single(groups);
        ModEntry modEntry = Assert.Single(mods);
        Assert.Equal("Colorful Icons", modEntry.Name);
        Assert.True(modEntry.IsWorkshopId);
    }

    [Fact]
    public void GeneratesStableUniqueSlugs()
    {
        var entries = new[]
        {
            Entry("pulsar", "a", "Owner/Repo", "Same Name"),
            Entry("pulsar", "b", "Owner/Other", "Same Name")
        };

        var (groups, _) = PluginIdentity.Build(entries);

        Assert.Equal(2, groups.Select(g => g.Slug).Distinct().Count());
    }
}
