using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using QM_Gallery.Components;
using QM_Gallery.Models;
using QM_Gallery.Services;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GalleryOptions>(
    builder.Configuration.GetSection(GalleryOptions.SectionName));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/xml", "text/plain", "image/svg+xml"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = System.IO.Compression.CompressionLevel.Fastest);

builder.Services.AddHttpClient(HttpClients.HubArchive, client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("qm-gallery/1.0");
});

builder.Services.AddHttpClient(HttpClients.GitHubApi, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("qm-gallery/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All
});

builder.Services.AddHttpClient(HttpClients.GitHubWeb, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("qm-gallery/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = DecompressionMethods.All
});

builder.Services.AddHttpClient(HttpClients.RawContent, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("qm-gallery/1.0");
});

builder.Services.AddHttpClient(HttpClients.Stats, client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("qm-gallery/1.0");
});

builder.Services.AddSingleton<StorePaths>();
builder.Services.AddSingleton<GitHubTokenProvider>();
builder.Services.AddSingleton<GitHubClient>();
builder.Services.AddSingleton<HubManifestService>();
builder.Services.AddSingleton<StatsClient>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddSingleton<ReadmeService>();
builder.Services.AddSingleton<GalleryStore>();
builder.Services.AddSingleton<EnrichmentService>();
builder.Services.AddHostedService<CatalogRefreshService>();

var app = builder.Build();

var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseResponseCompression();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/healthz", (GalleryStore store, GitHubClient github) =>
{
    GalleryView view = store.View;
    return Results.Json(new
    {
        status = view.Plugins.Count > 0 ? "ok" : "starting",
        plugins = view.Plugins.Count,
        mods = view.Mods.Count,
        authors = view.Plugins.Select(p => p.Author).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        lastManifestRefresh = view.Snapshot.LastManifestRefresh,
        lastStatsRefresh = view.Snapshot.LastStatsRefresh,
        tokenConfigured = github.TokenConfigured && !github.TokenInvalid,
        githubRemaining = github.Remaining
    });
});

app.MapGet("/robots.txt", (HttpRequest request, IOptions<GalleryOptions> options) =>
{
    string baseUrl = BaseUrl(request, options.Value);
    string body = $"User-agent: *\nAllow: /\n\nSitemap: {baseUrl}/sitemap.xml\n";
    return Results.Text(body, "text/plain", Encoding.UTF8);
});

app.MapGet("/sitemap.xml", (HttpRequest request, GalleryStore store, IOptions<GalleryOptions> options) =>
{
    string baseUrl = BaseUrl(request, options.Value);
    GalleryView view = store.View;

    var builder = new StringBuilder();
    builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    builder.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");

    void Add(string path, DateTimeOffset? modified = null)
    {
        builder.Append("  <url><loc>")
            .Append(SecurityElement.Escape(baseUrl + path))
            .Append("</loc>");
        if (modified is not null)
            builder.Append("<lastmod>").Append(modified.Value.ToString("yyyy-MM-dd")).Append("</lastmod>");
        builder.Append("</url>\n");
    }

    Add("/");
    Add("/plugins");
    Add("/mods");
    Add("/authors");
    Add("/about");
    Add("/status");

    foreach (PluginGroup group in view.Plugins.Where(g => !g.IsHidden))
        Add($"/plugins/{group.Slug}", group.Updated);

    builder.Append("</urlset>\n");
    return Results.Text(builder.ToString(), "application/xml", Encoding.UTF8);
});

app.Run();

static string BaseUrl(HttpRequest request, GalleryOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        return options.PublicBaseUrl.TrimEnd('/');

    return $"{request.Scheme}://{request.Host}".TrimEnd('/');
}
