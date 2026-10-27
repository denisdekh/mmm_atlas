# MMM Atlas

MMM Atlas is an ASP.NET Core website for mapping games by their Micro, Meso,
and Macro characteristics. The current development build includes a local
SQLite database, Steam integration, voting endpoints, and a browser-based
community vote panel on the home page.

## Prerequisites

Install these dependencies before cloning and running the project:

- [Git](https://git-scm.com/downloads)
- [.NET SDK 10.0.103](https://dotnet.microsoft.com/download/dotnet/10.0)
  - `global.json` pins this SDK feature band and permits newer patch releases.
  - The SDK includes the ASP.NET Core runtime.
- A current web browser

No separate SQLite installation, Node.js installation, or Docker service is
required. The SQLite native runtime and all other application libraries are
restored through NuGet.

The project also restores the repository-local `dotnet-ef` 10.0.10 tool. It is
not required for an ordinary development run, but it will be used for future
EF Core migrations.

## Clone and run

From a terminal:

```powershell
git clone <repository-url>
cd MMM_Atlas
dotnet --version
dotnet restore MMMAtlas.sln
dotnet tool restore
dotnet user-secrets --project MMMAtlas/MMMAtlas.csproj set "Steam:ApiKey" "<your-key>"
dotnet build MMMAtlas.sln -c Development
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Development --launch-profile http
```

Replace `<repository-url>` with the repository's Git URL.

Open [http://localhost:5163](http://localhost:5163). The home page contains a
development panel that calls the game list, detail, image, and vote APIs.

The launch profile sets `ASPNETCORE_ENVIRONMENT=Development`. The Development
build automatically:

1. Creates `MMMAtlas/App_Data/mmm-atlas-development.db`.
2. Applies the schema in `SQLiteTableSchemas.sql`.

The generated database and local ASP.NET data-protection keys are ignored by
Git.

The frontend reads rated games and votes from the main application tables.
Development seed data is not loaded by default.

## Build modes

Application behavior is selected at compile time rather than inferred from
`ASPNETCORE_ENVIRONMENT`:

| Configuration | Debug | Database | Test features |
| --- | --- | --- | --- |
| `Development` | Enabled | `mmm-atlas-development.db` | Enabled |
| `Test` | Disabled, optimized | `mmm-atlas-development.db` | Enabled |
| `Production` | Disabled, optimized | `mmm-atlas.db` | Disabled |

`Debug` remains a compatibility alias for `Development`, and `Release` remains
an alias for `Production`. New commands and deployment configuration should use
the explicit names above. The central `AtlasBuildPolicy` is the gate for future
test-only features; do not use the runtime environment as a feature flag.
Every mode applies the idempotent database schema at startup. Only Development
and Test builds may load the optional development seed data.

The footer feedback form accepts anonymous submissions of up to 1,000
characters. It stores the message, source IP address, and UTC creation time in
the `Feedback` table. The endpoint allows three submissions per source IP every
ten minutes. If the app is behind a reverse proxy, add each trusted proxy IP to
`Feedback:TrustedProxies` (for example,
`Feedback__TrustedProxies__0=10.0.0.5`) so the forwarded client IP and
original HTTPS scheme are used. The reverse proxy must send
`X-Forwarded-For` and `X-Forwarded-Proto`; the latter keeps Steam's sign-in
callback URL on HTTPS. Only configured proxy IPs (and the default local proxies)
are trusted for these headers.

Build a production artifact with:

```powershell
dotnet publish MMMAtlas/MMMAtlas.csproj -c Production
```

The HTTP-only `production` launch profile does not redirect to HTTPS. HTTPS
redirection is enabled automatically when the app has an HTTPS URL or Kestrel
endpoint. For TLS terminated by a reverse proxy, enable app-level redirects
explicitly with `HttpsRedirection__Enabled=true`; set
`HttpsRedirection__HttpsPort` when the public port is not 443.

To opt into the retained seed script for UI testing, set the development
configuration flag before starting the application:

```powershell
$env:DevelopmentData__SeedOnStartup = "true"
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Development --launch-profile http
```

The idempotent `MMMAtlas/Data/DevelopmentSeed.sql` script loads four games with
test images, four users, twelve votes, ownership records, and aggregate
statistics. Clear the environment variable or set it to `false` to return to
the normal startup behavior.

## Atlas grouping

`Atlas:Grouping` in `MMMAtlas/appsettings.json` controls how nearby scores group
as the graph zooms. Distances are score fractions: `0.01` is one percentage
point on each Micro, Meso, and Macro axis.

- `MaximumDistance` (default `0.01`): grouping distance at zoom 1 and below.
- `MinimumDistance` (default `0.0005`): lower limit at high zoom.
- `ZoomScalingFactor` (default `1`): exponent in
  `MaximumDistance / max(1, zoom)^ZoomScalingFactor`, clamped to the minimum
  and maximum. Larger values split groups sooner; `0` keeps the distance fixed.

With the defaults, zoom 2 uses `0.005`, zoom 10 uses `0.001`, and zoom 20 and
above uses `0.0005`. Games split when their group's score span exceeds the
current distance and merge again on zoom out. Equal-score games remain grouped.
Distances must be between 0 and 1, with minimum no greater than maximum; the
scaling factor must be finite and nonnegative. Invalid individual values use
defaults; distances above 1 and a minimum above the maximum are clamped.

Environment overrides use names such as
`Atlas__Grouping__ZoomScalingFactor=1.5`. Reload the Atlas page after changing
configuration.

## Steam library type filters

`Steam:LibraryFilters` in `MMMAtlas/appsettings.json` controls visibility and
background classification of owned Steam apps:

- `RequireGameType`: hide games whose nullable integer `SteamGames.hasParentApp` is `1`, indicating a non-null StoreBrowse `response.store_items[].related_items.parent_appid`. Application type alone does not exclude an item.
- `ExcludeBeta`: hide games whose nullable integer `SteamGames.isBeta` is `1`, indicating PICS `common.type = Beta`, ignoring case. The checked-in settings currently disable this filter.

Import saves every app returned by `GetOwnedGames`, including ownership, store
metadata, and artwork. New games start with both flags `NULL`; refreshes retain
known flags. Dashboard, library/search/pagination, game details, and Atlas reads
exclude only confirmed `1` flags for enabled filters. `NULL` and `0` remain
eligible, and game names are not used for classification. Filtered catalog and
ownership records stay in the database.

A hosted worker runs one metadata batch at startup and then every 30 seconds.
It fetches parent metadata only when `RequireGameType` is enabled and Beta
metadata only when `ExcludeBeta` is enabled, and fills only unknown values.
Missing/failed lookups leave their flags `NULL` for retry, without failing library
imports. The worker rotates through pending game IDs so unavailable apps do not
block later games. Classification results are stored in the database; neither
metadata client uses a memory cache. PICS uses an anonymous SteamKit2 WebSocket connection; no
Steam username or password is needed.

Configure `Steam:Metadata:BatchSize` (default `100`, allowed `1`–`1000`) in an
appsettings file, through `Steam__Metadata__BatchSize`, or with launch parameters:

```powershell
dotnet run --project MMMAtlas -- --Steam:Metadata:BatchSize=50
# Published application:
dotnet MMMAtlas.dll --Steam:Metadata:BatchSize=50
```

Override filter settings with `Steam__LibraryFilters__RequireGameType` and
`Steam__LibraryFilters__ExcludeBeta`, or in an environment's appsettings file.
Reloaded settings apply to subsequent reads and batches. A disabled filter
leaves its unknown flags untouched and does not hide its known `1` values.

When a metadata update excludes a game, setting the flags, deleting every vote
for that game, and removing its now-empty aggregate happen in one SQLite
transaction. A cleanup failure rolls everything back. New votes for excluded
games are rejected, and a notification after commit removes the game from open
Atlas views. Enabling a filter later processes its still-unknown flags; disabling
a filter does not restore deleted votes. Hosting must permit outbound Steam
connection-manager and Web API traffic.

The dashboard's Data collection section includes a Refresh Steam library button
when collection is enabled. Its authenticated, antiforgery-protected
`POST /api/me/steam-data/refresh` endpoint permits two requests per user in a
30-minute sliding window. The limit is held in server memory. Successful
refreshes reload the dashboard; rate-limited requests show a toast without
contacting Steam.

Dashboard artwork tries the Steam library card URL, the stored library card
backup, and then the store thumbnail. A game is hidden only when all three
sources fail. Hidden entries do not shift pagination offsets, and the grid
loads replacement games when available. Store thumbnails are fetched through
the app and cached in memory for one hour; failed lookups retry after five
minutes. For first votes, a thumbnail is padded into a portrait JPEG so it can
be used by the existing icon crop flow.

## Log files and verbosity

Logs are written to the console and newline-delimited JSON files in
`MMMAtlas/App_Data/Logs/mmm-atlas-YYYYMMDD.log` when running from the project.
Published apps use `App_Data/Logs` under their content root. Each event retains
its timestamp, severity, category, structured IDs, outcomes, latency, and exception
when supplied by the log call. Files roll daily and at 10 MiB; the newest 14 files
are retained (about 140 MiB, with possible single-event overshoot). Logs are ignored
by Git. The process needs write permission to the log directory.

The Development runtime profile defaults to `Debug`; Production defaults to
`Information`. Microsoft and System categories default to `Warning` to reduce
noise. Set `Logging:LogLevel:Default` or individual category levels in
`appsettings.json` / `appsettings.Development.json`. Valid values are `Trace`,
`Debug`, `Information`, `Warning`, `Error`, `Critical`, and `None`.

Override settings at launch (arguments after `--` are passed to the app):

```powershell
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Development --launch-profile http -- --Logging:LogLevel:Default=Trace
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Production --launch-profile production -- --Logging:LogLevel:Default=Warning
```

Alternatively set `Logging__LogLevel__Default=Debug` in environment variables
(including Visual Studio launch-profile properties). Command-line arguments take
precedence over environment variables, which override appsettings files.
Runtime environment selects logging defaults; `-c` still selects the app's build
mode, so pair the Production build with the production launch profile.

`Logging:File:Path`, `FileSizeLimitBytes`, `RetainedFileCountLimit`, and `Enabled`
configure file output; relative paths resolve against the app content root.
For example, `--Logging:File:Path=C:/Logs/Atlas/mmm-atlas-.log` changes its location.
File settings require a restart; standard log-level settings support appsettings
reload. Console logging remains available if file output is disabled.

## Canvas load testing

Launch the home-page Canvas with 3,000 deterministic synthetic points:

```powershell
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Test --launch-profile canvas-test
```

The test launch opens on port 5164 and exposes point-count and random-seed
controls above the Canvas. Applying the controls updates the current URL, so a
specific distribution can be reloaded or shared locally.

Test mode can also be enabled in Development and Test builds with these query
parameters:

```text
?atlasTest=1&atlasTestPoints=3000&atlasTestSeed=launch
```

`atlasTestPoints` accepts 0 through 20,000. Synthetic points use the normal
filter, hover, zoom, collision, density, and icon-loading paths, but they never
call the game-detail API or appear in the accessible real-game result list.
Omit the parameters to keep the test controls hidden. Production builds omit
the controls and ignore all test-mode query parameters.

## API performance profiling

`MMMAtlas.Profiler` simulates persistent concurrent clients against a running
Kestrel server. Reader clients poll the atlas and occasionally open a game and
its public-vote list. Writer clients authenticate as seeded users, submit votes,
and amend them repeatedly. Each run produces a standalone HTML report containing
failure rate, throughput, latency percentiles, endpoint breakdowns, response
volume, status/error groups, process CPU and memory, and a post-run vote-aggregate
consistency check.

Profiler authentication and consistency routes are tied to `AtlasBuildPolicy`.
They are available only in Development and Test builds, are disabled by default,
and require an access token of at least 16 UTF-8 bytes. Production builds never
map these routes, even if profiler settings are supplied.

Start a Test server in one terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Steam__ApiKey = "local-profiler-placeholder"
$env:ApiProfiler__Enabled = "true"
$env:ApiProfiler__AccessToken = "replace-with-a-local-random-token"
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Test --launch-profile http
```

Use a placeholder Steam key only for a seeded local profile. Use the normal
secret-store key when the scenario involves Steam API calls.

Run the profiler from a second terminal with the same token:

```powershell
$env:MMM_ATLAS_PROFILER_TOKEN = "replace-with-a-local-random-token"
dotnet run --project MMMAtlas.Profiler/MMMAtlas.Profiler.csproj -c Test -- `
  --base-url http://localhost:5163 `
  --connections 100 `
  --writer-ratio 10% `
  --duration-seconds 120 `
  --database-mode seed `
  --target-games 1000 `
  --target-users 1000 `
  --target-votes 5000 `
  --output artifacts/api-profile.html
```

When `ApiProfiler:Enabled` is true in a Development or Test build, the whole
application uses the dedicated `ConnectionStrings:MMMAtlasProfiler` SQLite file
(`App_Data/mmm-atlas-profiler.db` by default). Startup rejects a profiler
connection that resolves to either the normal Development or Production file.
Profiler setup therefore cannot clear or seed the application's ordinary data.

The default `seed` database mode clears the dedicated file's rows and creates
exact targets of 1,000 games, 1,000 users, and 5,000 unique votes. Synthetic
games include representative names, descriptions, developer, publisher, genre,
Steam metadata, and blob-backed SVG, atlas-icon, and library-card placeholders;
votes are deterministic and their aggregates are rebuilt before traffic starts.
Targets may each be set from 0 through 10,000. To measure
an empty-atlas read workload, use `--database-mode empty --writer-ratio 0`.

`grow` mode measures the application while its database changes size. It resets
and prepopulates `--initial-games`, then reserves `--game-writer-ratio` of writer
clients for calls to the gated profiler game-creation endpoint until
`--target-games` is reached. Those calls atomically add synthetic Steam/store
data, library ownership, image blobs, a first vote, and its aggregate without
contacting Steam or its CDN. Other writer clients continue submitting and
changing votes while readers poll the growing atlas and fetch game details,
votes, icons, and library cards. For example:

```powershell
dotnet run --project MMMAtlas.Profiler/MMMAtlas.Profiler.csproj -c Test -- `
  --database-mode grow `
  --initial-games 1000 --target-games 2000 `
  --target-users 1500 --target-votes 5000 `
  --connections 100 --writer-ratio 20% --game-writer-ratio 25%
```

By default, the game-creation cadence is calculated so growth spans roughly 90%
of the measured duration; override it with
`--game-creation-interval-seconds`. The HTML report plots game count over time
and groups failure and latency percentiles into database-size bands. A grow run
returns a nonzero exit code if it cannot reach the target within the configured
duration; that is a useful capacity result rather than silently extending the
measurement window.
`--database-mode keep` skips setup and runs against the dedicated file exactly
as the previous profile left it. Writer activity persists in that file; the
next `seed`, `grow`, or `empty` run clears it.

The total client count and writer/reader ratio are controlled by
`--connections` and `--writer-ratio`. Run with `--help` for cadence, timeout,
seed, HTTPS, user-ID, and server-process controls. Pass the API server PID with
`--server-pid` to add server CPU, working set, private memory, and thread counts;
the load generator is always sampled.

The profiler's reader workload models the browser's fallback/recovery path:
every graph viewer requests `/api/games/atlas` once every 45 seconds. The steady
fallback reading load is thus:

```text
atlas polls/second = reader clients / 45
```

With the default 100 clients and 10% writers, 90 readers produce 2 atlas polls
per second. Occasional game-detail behavior (20% of poll cycles, four requests
for detail, public votes, atlas icon, and library card) raises the calculated
default to 3.6 total reader requests per second. Both expected values are printed
before a run and recorded in the HTML report. For the 1,800-user planning
scenario, a 5% writer ratio gives 1,710 readers, or 38 fallback atlas polls per
second before detail views. Under normal browser operation, the atlas loads the
list once, holds one `/api/atlas/events` SSE connection, and fetches only the
changed game from `/api/games/{gameId}` after a `game-updated` event.

## Live atlas events

`GET /api/atlas/events` is a public Server-Sent Events stream. It sends a
`game-updated` event with JSON such as `{ "gameId": 123 }` after a successful
vote commits, plus a comment heartbeat every 15 seconds. The event is only an
invalidation notice; clients fetch authoritative data from the individual-game
API. Manual Refresh remains available, and browsers fall back to 45-second
whole-atlas polling while SSE is unavailable or disconnected.

The in-process broadcaster reaches only SSE clients connected to the same
application instance. A multi-instance deployment needs a shared backplane or
broker (or instance affinity with an accepted possibility of missed events).
Because reconnect recovery refreshes the atlas, missed notifications do not
permanently leave a viewer stale.

Reverse proxies must allow long-lived streaming responses, avoid buffering
`text/event-stream`, and use idle timeouts longer than the 15-second heartbeat.
The application sends `Cache-Control: no-cache` and `X-Accel-Buffering: no`;
equivalent buffering/timeout settings may still be required for non-Nginx
proxies and hosting platforms.

## HTTPS development

HTTPS is optional for the current local API and UI work. To use it, trust the
.NET development certificate once:

```powershell
dotnet dev-certs https --trust
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Development --launch-profile https
```

Then open [https://localhost:7176](https://localhost:7176).

On Linux, certificate trust may require browser- or distribution-specific
configuration. The HTTP launch profile remains available.

## Run tests

```powershell
dotnet test MMMAtlas.sln -c Test
```

To build and test without restoring packages again:

```powershell
dotnet build MMMAtlas.sln -c Test --no-restore
dotnet test MMMAtlas.sln -c Test --no-build --no-restore
```

## Reset the development data

Stop the application before resetting the database.

PowerShell:

```powershell
Remove-Item -LiteralPath MMMAtlas/App_Data/mmm-atlas-development.db -ErrorAction SilentlyContinue
Remove-Item -LiteralPath MMMAtlas/App_Data/mmm-atlas-development.db-shm -ErrorAction SilentlyContinue
Remove-Item -LiteralPath MMMAtlas/App_Data/mmm-atlas-development.db-wal -ErrorAction SilentlyContinue
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Development --launch-profile http
```

Bash:

```bash
rm -f MMMAtlas/App_Data/mmm-atlas-development.db
rm -f MMMAtlas/App_Data/mmm-atlas-development.db-shm
rm -f MMMAtlas/App_Data/mmm-atlas-development.db-wal
dotnet run --project MMMAtlas/MMMAtlas.csproj -c Development --launch-profile http
```

The next Development startup recreates the schema. It remains empty unless
real Steam activity populates it or `DevelopmentData__SeedOnStartup` is
explicitly enabled.

## Restored application packages

`dotnet restore` installs the project-managed dependencies, including:

- ASP.NET Security OpenID Steam provider 10.0.0
- Entity Framework Core SQLite and Design packages 10.0.10
- SQLitePCLRaw native SQLite bundle 2.1.12
- AngleSharp 1.5.2
- SteamKit2 3.4.0
- MSTest and ASP.NET Core test-host packages used by `MMMAtlas.Tests`

The application requires a Steam Web API key at startup. Store it under
`Steam:ApiKey` with .NET user-secrets for local development, as shown above,
and use the deployment platform's secret store in production. Do not put the
key in an appsettings file or commit it to source control.
