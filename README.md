# QM Gallery

A read-only gallery for Space Engineers plugins published through the **Pulsar**, **Magnetar** and **Quasar** hubs.

There is no login, no publishing and no admin area. The gallery mirrors the public hub descriptors, pins them to the exact
commit each hub ships, and adds READMEs, commit dates and community statistics.

## Requirements

**Docker (recommended):**

- Docker Engine or Docker Desktop with Compose v2

**Local development:**

- [.NET SDK 11](https://dotnet.microsoft.com/download)

A GitHub token is optional in both cases. Without one, manifests, commits, READMEs and statistics still work; stars and
licences are omitted.

## Install with Docker

From the project root:

```bash
cd "C:\Development\Development\Public\QM Gallery"   # or your checkout path
docker compose up -d --build
```

Open **http://localhost:8080**.

Useful commands:

```bash
docker compose ps
docker compose logs -f gallery
curl http://localhost:8080/healthz
docker compose down
```

The catalogue is cached in the named volume `qm-gallery-data` at `/data`, so it survives container re-creation.
The first start downloads the three hub archives and begins resolving commit dates and READMEs in the background; the
catalogue is usable immediately and fills in over the next minute or two.

### Change the host port

`docker-compose.yml` maps host `8080` to container `8080`:

```yaml
    ports:
      - "8080:8080"
```

Change only the left number, then run `docker compose up -d` and open the new port. If you use a reverse proxy, bind to
loopback instead: `"127.0.0.1:8080:8080"`.

### Run without a GitHub token

The Compose file expects the optional secret file to exist. If you don't want a token, create an empty placeholder:

```bash
mkdir -p secrets
touch secrets/github_token.txt
```

The app treats an empty file as "no token" and runs normally. Alternatively, remove the `Gallery__GitHubTokenFile`
environment line and the `./secrets/github_token.txt` volume from `docker-compose.yml`.

### Add a GitHub token (optional)

Stars, licences and repository freshness need a token. Use a **fine-grained personal access token**:

- Repository access: **Public repositories (read-only)**
- Permissions: **Metadata (read-only)** only

Create the secret file next to `docker-compose.yml`, readable by the container user (UID 1000):

```bash
mkdir -p secrets
chmod 700 secrets

read -rsp "Paste token: " GH_TOKEN; echo
umask 077
printf '%s' "$GH_TOKEN" > secrets/github_token.txt
unset GH_TOKEN

chown 1000:1000 secrets/github_token.txt
chmod 400 secrets/github_token.txt
```

If your host user is not UID 1000, substitute the UID that runs the container.

`docker-compose.yml` mounts the file read-only at `/run/secrets/github_token` and passes only its **path** to the app
(`Gallery__GitHubTokenFile`). The token is not baked into the image and is never written to logs, the cache or the browser.
The app re-reads the file periodically, so rotating it does not require a rebuild.

**Never commit the token.** `.gitignore` already excludes `secrets/`, `.env` and `App_Data/`. The repository contains only
the placeholder `<paste-token>` shown in the local-development section below.

If the token is missing, expired or rejected, the gallery keeps working anonymously and only hides stars/licences.

## Run locally

```bash
dotnet run
```

Open **http://localhost:5135** (see `Properties/launchSettings.json`).

The cache is written to `App_Data/cache` by default. Delete that folder to force a complete re-fetch.

### Local GitHub token (optional)

Keep it out of the repo with user-secrets:

```bash
dotnet user-secrets set "Gallery:GitHubToken" "<paste-token>"
```

This stores the value under your user profile, not in the repository. You can also set
`Gallery__GitHubTokenFile` to a path outside the repository, or use the environment variable
`Gallery:GitHubToken` for a one-off run. Prefer user-secrets or a file; do not put a token in `appsettings.json`.

### Tests

```bash
dotnet test "QM Gallery.slnx"
```

### Publish without Docker

```bash
dotnet publish "QM Gallery.csproj" -c Release -o out
ASPNETCORE_URLS=http://0.0.0.0:8080 \
Gallery__CacheDirectory=/var/lib/qm-gallery \
dotnet out/QM_Gallery.dll
```

Make sure the cache directory is writable by the user running the process.

## Configuration

Settings live under `Gallery` in `appsettings.json` and can be overridden with environment variables
(`Gallery__CacheDirectory`, `Gallery__PublicBaseUrl`, ...).

| Setting | Default | Purpose |
| --- | --- | --- |
| `CacheDirectory` | `App_Data/cache` | Snapshot, READMEs and missing markers. In Docker this is `/data`. |
| `ManifestRefreshMinutes` | `30` | How often the hub archives are re-read. |
| `StatsHours` | `6` | Pulsar/Magnetar statistics refresh. |
| `RepoMetadataHours` | `24` | GitHub stars/licence refresh (token only). |
| `ReadmeRetryHours` | `168` | Retry a missing README after this many hours. |
| `EnrichmentConcurrency` | `6` | Parallel GitHub requests. |
| `ReadmeConcurrency` | `3` | Parallel README downloads. |
| `GitHubTokenFile` | empty | Path to the secret file (`/run/secrets/github_token` in Docker). |
| `GitHubToken` | empty | Development fallback; prefer user-secrets or the file. |
| `PublicBaseUrl` | empty | Absolute base URL used by the sitemap and robots.txt. Set this when behind a domain. |
| `DiscordUrl` | community invite | Discord link in the header, footer and help states. |

For Docker behind a domain, set the public URL when starting Compose:

```bash
PUBLIC_BASE_URL=https://plugins.example.com docker compose up -d
```

The three hubs, their statistics endpoints and their descriptions are also in `appsettings.json`, so adding a fourth hub
is a configuration change rather than a code change.

## What it shows

- Every plugin descriptor from `StarCpt/PluginHub`, `CometWorks/magnetar-hub` and `CometWorks/quasar-hub`.
- One entry per real plugin, merged across hubs, with system badges and legacy-id aliases folded in.
- The pinned commit date (the version the hub actually ships), message and hash.
- READMEs rendered from the pinned commit, with relative links and images rewritten to that commit.
- Pulsar votes, ratings and 30-day player counts, plus Magnetar server usage.
- Optional GitHub stars, licence, archived state and repository freshness when a token is configured.
- A separate `/mods` view for the Workshop mods registered with the Pulsar hub.

## Caching

- The catalogue snapshot is written atomically to `snapshot.json` and loaded on startup.
- Commit metadata and READMEs are immutable per commit, so they are cached forever.
- Repo metadata, statistics and manifests have TTLs and refresh in the background.
- A failed hub refresh keeps the last good copy instead of emptying the catalogue.
- Stale data is served immediately while a refresh runs; requests never wait for GitHub.

## Endpoints

| Path | Purpose |
| --- | --- |
| `/` | Home, search and highlights |
| `/plugins` | Catalogue with filters, sorting and paging |
| `/plugins/{slug}` | README, metadata, install steps and manifest |
| `/mods` | Pulsar Workshop mods |
| `/authors` | Author index |
| `/about` | How the catalogue works |
| `/status` | Data freshness and source health |
| `/healthz` | JSON health check |
| `/sitemap.xml`, `/robots.txt` | SEO |

## Notes

- Plugin names always come from the hub descriptor (`FriendlyName`); the repository is shown on the detail page.
- Hidden dependency plugins can be revealed with the "Show hidden" filter.
- Quasar UI plugins have no community rating service. No score is invented for them.
- This is an unofficial community project, not affiliated with Keen Software House, Pulsar, Magnetar or Quasar.
