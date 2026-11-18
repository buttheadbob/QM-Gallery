using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

/// <summary>
/// Resolves the optional GitHub token from configuration or a secret file.
/// The value is never logged, serialized or exposed to the browser.
/// </summary>
public sealed class GitHubTokenProvider
{
    private readonly GalleryOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GitHubTokenProvider> _logger;

    private readonly object _sync = new();
    private string? _token;
    private DateTime _loadedFileWriteUtc;
    private DateTime _lastFileCheckUtc;
    private bool _invalid;
    private bool _initialized;

    public GitHubTokenProvider(
        IOptions<GalleryOptions> options,
        IHostEnvironment environment,
        ILogger<GitHubTokenProvider> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public bool Configured
    {
        get
        {
            EnsureLoaded();
            return !string.IsNullOrWhiteSpace(_token);
        }
    }

    public bool Invalid
    {
        get
        {
            EnsureLoaded();
            return _invalid;
        }
    }

    public string? GetToken()
    {
        EnsureLoaded();
        return _invalid ? null : _token;
    }

    public void MarkInvalid()
    {
        lock (_sync)
        {
            if (!_invalid)
                _logger.LogWarning("GitHub token was rejected; continuing without repository metadata");
            _invalid = true;
        }
    }

    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                _initialized = true;
                LoadTokenLocked(force: true);
                return;
            }

            string? file = ResolveFile();
            if (file is null)
                return;

            // Re-read the secret file at most once a minute so rotation needs no restart.
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFileCheckUtc).TotalSeconds < 60)
                return;

            _lastFileCheckUtc = now;
            LoadTokenLocked(force: false);
        }
    }

    private void LoadTokenLocked(bool force)
    {
        string? file = ResolveFile();

        if (file is not null)
        {
            try
            {
                if (!File.Exists(file))
                {
                    if (force)
                        _logger.LogWarning("GitHub token file was not found; running without repository metadata");
                    return;
                }

                DateTime writeUtc = File.GetLastWriteTimeUtc(file);
                if (!force && writeUtc == _loadedFileWriteUtc)
                    return;

                string value = File.ReadAllText(file).Trim();
                _loadedFileWriteUtc = writeUtc;
                _token = string.IsNullOrWhiteSpace(value) ? null : value;
                _invalid = false;
                _logger.LogInformation("GitHub token loaded from secret file");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not read the GitHub token file: {Message}", ex.Message);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.GitHubToken))
        {
            _token = _options.GitHubToken.Trim();
            _invalid = false;
            _logger.LogInformation("GitHub token loaded from configuration");
        }
    }

    private string? ResolveFile()
    {
        string? configured = _options.GitHubTokenFile;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("GITHUB_TOKEN_FILE");

        if (string.IsNullOrWhiteSpace(configured))
            return null;

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configured));
    }
}
