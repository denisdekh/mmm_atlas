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
dotnet build MMMAtlas.sln
dotnet run --project MMMAtlas/MMMAtlas.csproj --launch-profile http
```

Replace `<repository-url>` with the repository's Git URL.

Open [http://localhost:5163](http://localhost:5163). The home page contains a
development panel that calls the game list, detail, image, and vote APIs.

The launch profile sets `ASPNETCORE_ENVIRONMENT=Development`. Development mode
automatically:

1. Creates `MMMAtlas/App_Data/mmm-atlas-development.db`.
2. Applies the schema in `SQLiteTableSchemas.sql`.

The generated database and local ASP.NET data-protection keys are ignored by
Git.

The frontend reads rated games and votes from the main application tables.
Development seed data is not loaded by default.

To opt into the retained seed script for UI testing, set the development
configuration flag before starting the application:

```powershell
$env:DevelopmentData__SeedOnStartup = "true"
dotnet run --project MMMAtlas/MMMAtlas.csproj --launch-profile http
```

The idempotent `MMMAtlas/Data/DevelopmentSeed.sql` script loads four games with
test images, four users, twelve votes, ownership records, and aggregate
statistics. Clear the environment variable or set it to `false` to return to
the normal startup behavior.

## Canvas load testing

Launch the home-page Canvas with 3,000 deterministic synthetic points:

```powershell
dotnet run --project MMMAtlas/MMMAtlas.csproj --launch-profile canvas-test
```

The test launch opens on port 5164 and exposes point-count and random-seed
controls above the Canvas. Applying the controls updates the current URL, so a
specific distribution can be reloaded or shared locally.

Test mode can also be enabled on any launch with these query parameters:

```text
?atlasTest=1&atlasTestPoints=3000&atlasTestSeed=launch
```

`atlasTestPoints` accepts 0 through 20,000. Synthetic points use the normal
filter, hover, zoom, collision, density, and icon-loading paths, but they never
call the game-detail API or appear in the accessible real-game result list.
Omit the parameters to keep the test controls hidden.

## HTTPS development

HTTPS is optional for the current local API and UI work. To use it, trust the
.NET development certificate once:

```powershell
dotnet dev-certs https --trust
dotnet run --project MMMAtlas/MMMAtlas.csproj --launch-profile https
```

Then open [https://localhost:7176](https://localhost:7176).

On Linux, certificate trust may require browser- or distribution-specific
configuration. The HTTP launch profile remains available.

## Run tests

```powershell
dotnet test MMMAtlas.sln
```

To build and test without restoring packages again:

```powershell
dotnet build MMMAtlas.sln --no-restore
dotnet test MMMAtlas.sln --no-build --no-restore
```

## Reset the development data

Stop the application before resetting the database.

PowerShell:

```powershell
Remove-Item -LiteralPath MMMAtlas/App_Data/mmm-atlas-development.db -ErrorAction SilentlyContinue
Remove-Item -LiteralPath MMMAtlas/App_Data/mmm-atlas-development.db-shm -ErrorAction SilentlyContinue
Remove-Item -LiteralPath MMMAtlas/App_Data/mmm-atlas-development.db-wal -ErrorAction SilentlyContinue
dotnet run --project MMMAtlas/MMMAtlas.csproj --launch-profile http
```

Bash:

```bash
rm -f MMMAtlas/App_Data/mmm-atlas-development.db
rm -f MMMAtlas/App_Data/mmm-atlas-development.db-shm
rm -f MMMAtlas/App_Data/mmm-atlas-development.db-wal
dotnet run --project MMMAtlas/MMMAtlas.csproj --launch-profile http
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
- MSTest and ASP.NET Core test-host packages used by `MMMAtlas.Tests`

The application requires a Steam Web API key at startup. Store it under
`Steam:ApiKey` with .NET user-secrets for local development, as shown above,
and use the deployment platform's secret store in production. Do not put the
key in an appsettings file or commit it to source control.
