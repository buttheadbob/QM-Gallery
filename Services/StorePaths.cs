using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

public sealed class StorePaths
{
    public StorePaths(IOptions<GalleryOptions> options, IHostEnvironment environment)
    {
        string configured = options.Value.CacheDirectory;
        Root = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));

        SnapshotFile = Path.Combine(Root, "snapshot.json");
        ReadmesDirectory = Path.Combine(Root, "readmes");
        MissingDirectory = Path.Combine(Root, "missing");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ReadmesDirectory);
        Directory.CreateDirectory(MissingDirectory);
    }

    public string Root { get; }
    public string SnapshotFile { get; }
    public string ReadmesDirectory { get; }
    public string MissingDirectory { get; }

    public string ReadmeFile(string key) => Path.Combine(ReadmesDirectory, key + ".md");
    public string MissingFile(string key) => Path.Combine(MissingDirectory, key + ".json");
}
