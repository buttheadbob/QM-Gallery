using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace QM_Gallery.Services;

/// <summary>
/// Renders third-party README markdown to safe HTML and rewrites relative links
/// to the exact commit the hub pinned.
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    private static readonly Regex AttributeUrl = new(
        @"(?<prefix>\b(?:href|src)\s*=\s*"")(?<url>[^""]*)(?<suffix>"")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExternalLink = new(
        "<a\\s+href=\"(?<url>https?://[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImageTag = new(
        "<img\\b(?![^>]*\\bloading=)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HtmlSanitizer _sanitizer;
    private readonly object _sanitizeLock = new();

    public MarkdownRenderer()
    {
        _sanitizer = new HtmlSanitizer();

        _sanitizer.AllowedTags.Clear();
        foreach (string tag in new[]
                 {
                     "p", "br", "hr", "div", "span", "a", "img", "strong", "b", "em", "i", "u",
                     "s", "del", "ins", "mark", "small", "sub", "sup", "code", "pre", "blockquote",
                     "ul", "ol", "li", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr",
                     "th", "td", "h1", "h2", "h3", "h4", "h5", "h6", "details", "summary", "kbd",
                     "figure", "figcaption", "input"
                 })
        {
            _sanitizer.AllowedTags.Add(tag);
        }

        _sanitizer.AllowedAttributes.Clear();
        foreach (string attribute in new[]
                 {
                     "href", "src", "alt", "title", "width", "height", "align", "colspan",
                     "rowspan", "id", "start", "type", "checked", "disabled", "loading"
                 })
        {
            _sanitizer.AllowedAttributes.Add(attribute);
        }

        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");

        _sanitizer.KeepChildNodes = true;
        _sanitizer.AllowedCssProperties.Clear();
    }

    public string Render(string markdown, string repoKey, string sha)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        string html = Markdown.ToHtml(markdown, Pipeline);
        html = RewriteUrls(html, repoKey, sha);
        lock (_sanitizeLock)
        {
            html = _sanitizer.Sanitize(html);
        }

        html = ExternalLink.Replace(
            html,
            "<a href=\"${url}\" target=\"_blank\" rel=\"noopener noreferrer\"");
        html = ImageTag.Replace(html, "<img loading=\"lazy\"");

        return html;
    }

    private static string RewriteUrls(string html, string repoKey, string sha)
    {
        var blobBase = new Uri($"https://github.com/{repoKey}/blob/{sha}/");
        var rawBase = new Uri($"https://raw.githubusercontent.com/{repoKey}/{sha}/");

        return AttributeUrl.Replace(html, match =>
        {
            string prefix = match.Groups["prefix"].Value;
            string value = match.Groups["url"].Value;
            string suffix = match.Groups["suffix"].Value;

            if (string.IsNullOrWhiteSpace(value))
                return match.Value;

            string url = value.Trim();
            if (url.StartsWith('#')
                || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            if (url.StartsWith("//", StringComparison.Ordinal))
                return prefix + "https:" + url + suffix;

            bool isImage = prefix.Contains("src", StringComparison.OrdinalIgnoreCase);
            Uri baseUri = isImage ? rawBase : blobBase;

            try
            {
                Uri resolved = url.StartsWith('/')
                    ? new Uri(baseUri, url.TrimStart('/'))
                    : new Uri(baseUri, url);
                return prefix + resolved.AbsoluteUri + suffix;
            }
            catch
            {
                return match.Value;
            }
        });
    }
}
