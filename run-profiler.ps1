# Trust the ASP.NET Core development HTTPS certificate once.
dotnet dev-certs https --trust

# Build both the server and profiler.
dotnet build MMMAtlas.sln -c Test

# Configure the Development/Test-only profiler.
$token = '736f6d652d72616e646f6d2d746f6b656e'

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Steam__ApiKey = "local-profiler-placeholder"
$env:ApiProfiler__Enabled = "true"
$env:ApiProfiler__AccessToken = $token
$env:MMM_ATLAS_PROFILER_TOKEN = $token

# Execute the built DLL directly so this PID is the actual API server process.
$serverDirectory = (Resolve-Path "MMMAtlas").Path
$server = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList @(
        "bin/Test/net10.0/MMMAtlas.dll",
        "--urls",
        "https://localhost:7176"
    ) `
    -WorkingDirectory $serverDirectory `
    -WindowStyle Hidden `
    -PassThru

Write-Host "API server PID: $($server.Id)"
Start-Sleep -Seconds 3

try {
    # Baseline: 100 readers against a completely empty database.
    dotnet run `
        --project MMMAtlas.Profiler/MMMAtlas.Profiler.csproj `
        -c Test --no-build --no-restore -- `
        --base-url https://localhost:7176 `
        --connections 100 `
        --writer-ratio 0 `
        --duration-seconds 10 `
        --ramp-up-seconds 5 `
        --database-mode empty `
        --server-pid $server.Id `
        --output artifacts/api-profile-0000-games.html

    # Measure the dedicated DB while it grows from 1,000 to 2,000 games.
    # Of 100 clients, 20 are writers: 5 create games while 15 submit or
    # amend votes. The other 80 poll and view the atlas throughout growth.
    dotnet run `
        --project MMMAtlas.Profiler/MMMAtlas.Profiler.csproj `
        -c Test --no-build --no-restore -- `
        --base-url https://localhost:7176 `
        --connections 400 `
        --writer-ratio 20% `
        --game-writer-ratio 25% `
        --duration-seconds 120 `
        --ramp-up-seconds 10 `
        --database-mode grow `
        --initial-games 50 `
        --target-games 2000 `
        --target-users 1000 `
        --target-votes 10000 `
        --server-pid $server.Id `
        --output artifacts/api-profile-50-to-2000-games.html
}
finally {
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
}
