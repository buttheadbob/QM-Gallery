using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using QM_Gallery.Models;

namespace QM_Gallery.Services;

public sealed class GitHubClient
{
    private const int PatchBytes = 64 * 1024;
    private readonly IHttpClientFactory _httpFactory;
    private readonly GitHubTokenProvider _tokens;
    private readonly ILogger<GitHubClient> _logger;

    private int? _remaining;
    private DateTimeOffset? _resetAt;
    private string? _lastError;

    public GitHubClient(
        IHttpClientFactory httpFactory,
        GitHubTokenProvider tokens,
        IOptions<GalleryOptions> options,
        ILogger<GitHubClient> logger)
    {
        _httpFactory = httpFactory;
        _tokens = tokens;
        _logger = logger;
    }

    public bool TokenConfigured => _tokens.Configured && !_tokens.Invalid;
    public bool TokenInvalid => _tokens.Invalid;
    public int? Remaining => _remaining;
    public DateTimeOffset? ResetAt => _resetAt;
    public string? LastError => _lastError;

    public bool BudgetAvailable =>
        _remaining is null || _remaining > 10 || (_resetAt is not null && _resetAt <= DateTimeOffset.UtcNow);

    public async Task<RepoInfo?> GetRepoAsync(string repoKey, CancellationToken cancellationToken)
    {
        if (!TokenConfigured || !BudgetAvailable)
            return null;

        using HttpResponseMessage? response = await SendApiAsync(
            $"https://api.github.com/repos/{repoKey}",
            cancellationToken);

        if (response is null)
            return null;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokens.MarkInvalid();
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                NoteFailure(response, $"repo {repoKey}");
                return null;
            }

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                JsonElement root = json.RootElement;

                var info = new RepoInfo
                {
                    RepoKey = repoKey,
                    Stars = GetInt(root, "stargazers_count"),
                    Forks = GetInt(root, "forks_count"),
                    OpenIssues = GetInt(root, "open_issues_count"),
                    License = root.TryGetProperty("license", out JsonElement license)
                        && license.ValueKind == JsonValueKind.Object
                        && license.TryGetProperty("spdx_id", out JsonElement spdx)
                        ? spdx.GetString()
                        : null,
                    Description = GetString(root, "description"),
                    Homepage = GetString(root, "homepage"),
                    Archived = root.TryGetProperty("archived", out JsonElement archived)
                        && archived.ValueKind == JsonValueKind.True,
                    Disabled = root.TryGetProperty("disabled", out JsonElement disabled)
                        && disabled.ValueKind == JsonValueKind.True,
                    DefaultBranch = GetString(root, "default_branch"),
                    PushedAt = ParseDate(GetString(root, "pushed_at")),
                    FetchedAt = DateTimeOffset.UtcNow,
                };

                if (root.TryGetProperty("topics", out JsonElement topics) && topics.ValueKind == JsonValueKind.Array)
                {
                    info.Topics = topics.EnumerateArray()
                        .Select(t => t.GetString())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t!)
                        .Take(12)
                        .ToList();
                }

                return info;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Could not parse repository metadata for {Repo}: {Message}", repoKey, ex.Message);
                return null;
            }
        }
    }

    public async Task<CommitInfo?> GetCommitAsync(
        string repoKey,
        string sha,
        CancellationToken cancellationToken)
    {
        if (!GitHubSha.IsMatch(sha))
        {
            return new CommitInfo
            {
                RepoKey = repoKey,
                Sha = sha,
                Failed = true,
                FetchedAt = DateTimeOffset.UtcNow,
                Message = "Invalid commit hash"
            };
        }

        CommitInfo? patch = await GetCommitFromPatchAsync(repoKey, sha, cancellationToken);
        if (patch is not null)
            return patch;

        if (!TokenConfigured || !BudgetAvailable)
            return null;

        using HttpResponseMessage? response = await SendApiAsync(
            $"https://api.github.com/repos/{repoKey}/commits/{sha}",
            cancellationToken);

        if (response is null)
            return null;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokens.MarkInvalid();
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                NoteFailure(response, $"commit {repoKey}@{sha[..Math.Min(8, sha.Length)]}");
                return null;
            }

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                JsonElement root = json.RootElement;

                string? dateText = null;
                string? message = null;
                if (root.TryGetProperty("commit", out JsonElement commit))
                {
                    message = GetString(commit, "message");
                    if (commit.TryGetProperty("author", out JsonElement author))
                        dateText = GetString(author, "date");
                }

                return new CommitInfo
                {
                    RepoKey = repoKey,
                    Sha = sha,
                    Date = ParseDate(dateText),
                    Message = FirstLine(message),
                    AuthorLogin = root.TryGetProperty("author", out JsonElement user)
                        && user.ValueKind == JsonValueKind.Object
                        ? GetString(user, "login")
                        : null,
                    Source = "api",
                    FetchedAt = DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return null;
            }
        }
    }

    public async Task<string?> GetDefaultBranchHeadAsync(string repoKey, string branch, CancellationToken cancellationToken)
    {
        if (!TokenConfigured || !BudgetAvailable)
            return null;

        using HttpResponseMessage? response = await SendApiAsync(
            $"https://api.github.com/repos/{repoKey}/commits/{Uri.EscapeDataString(branch)}",
            cancellationToken);

        if (response is null)
            return null;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                NoteFailure(response, $"branch {repoKey}");
                return null;
            }

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return GetString(json.RootElement, "sha");
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task<CommitInfo?> GetCommitFromPatchAsync(
        string repoKey,
        string sha,
        CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = _httpFactory.CreateClient(HttpClients.GitHubWeb);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://github.com/{repoKey}/commit/{sha}.patch");

            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] buffer = new byte[PatchBytes];
            int total = 0;
            int read;
            while (total < buffer.Length
                && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
            {
                total += read;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, total);
            string? dateText = null;
            string? subject = null;
            string? author = null;

            foreach (string line in text.Split('\n', 64))
            {
                if (line.StartsWith("Date:", StringComparison.Ordinal) && dateText is null)
                    dateText = line[5..].Trim();
                else if (line.StartsWith("Subject:", StringComparison.Ordinal) && subject is null)
                    subject = line[8..].Trim();
                else if (line.StartsWith("From:", StringComparison.Ordinal) && author is null)
                    author = line[5..].Trim();
            }

            if (dateText is null)
                return null;

            DateTimeOffset? date = ParseMailDate(dateText);
            if (date is null)
                return null;

            if (subject is not null && subject.StartsWith("[PATCH]", StringComparison.Ordinal))
                subject = subject[7..].Trim();

            return new CommitInfo
            {
                RepoKey = repoKey,
                Sha = sha,
                Date = date,
                Message = subject ?? "",
                AuthorName = author,
                Source = "patch",
                FetchedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Patch metadata failed for {Repo}@{Sha}: {Message}", repoKey, sha, ex.Message);
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendApiAsync(string url, CancellationToken cancellationToken)
    {
        string? token = _tokens.GetToken();
        if (token is null)
            return null;

        HttpClient client = _httpFactory.CreateClient(HttpClients.GitHubApi);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("qm-gallery/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        // Only ever attach the credential to the API host.
        if (request.RequestUri is { Host: "api.github.com" })
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        ReadRateLimit(response);
        return response;
    }

    private void ReadRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining)
            && int.TryParse(remaining.FirstOrDefault(), out int value))
        {
            _remaining = value;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? reset)
            && long.TryParse(reset.FirstOrDefault(), out long seconds))
        {
            _resetAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _lastError = response.StatusCode == HttpStatusCode.TooManyRequests
                ? "GitHub secondary rate limit"
                : "GitHub rate limit";
        }
    }

    private void NoteFailure(HttpResponseMessage response, string target)
    {
        if (!response.IsSuccessStatusCode)
        {
            _lastError = $"{(int)response.StatusCode} for {target}";
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                _logger.LogWarning("GitHub rate limit hit while reading {Target}", target);
            else
                _logger.LogDebug("GitHub request for {Target} returned {Status}", target, response.StatusCode);
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset result)
            ? result
            : null;

    private static DateTimeOffset? ParseMailDate(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            ["ddd, d MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss zzz"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset result)
            ? result
            : ParseDate(value);

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        int newline = value.IndexOf('\n');
        return (newline < 0 ? value : value[..newline]).Trim();
    }

    private static readonly Regex GitHubSha = new(
        "^[0-9a-fA-F]{7,40}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
