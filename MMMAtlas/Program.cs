using System.Security.Claims;
using System.Threading.RateLimiting;
using AspNet.Security.OpenId.Steam;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using MMMAtlas.Api;
using MMMAtlas.Configuration;
using MMMAtlas.Data;
using MMMAtlas.Observability;
using MMMAtlas.Steam;
using MMMAtlas.Voting;

var builder = WebApplication.CreateBuilder(args);
var buildPolicy = AtlasBuildPolicy.Current;
var httpsRedirection = HttpsRedirectionPolicy.From(builder.Configuration);
builder.WebHost.UseStaticWebAssets();
builder.Logging.AddAtlasFileLogging(builder.Configuration, builder.Environment.ContentRootPath);

var steamApiKey = builder.Configuration["Steam:ApiKey"]
    ?? throw new InvalidOperationException("Steam:ApiKey is not configured.");

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var address in builder.Configuration
        .GetSection("Feedback:TrustedProxies").Get<string[]>() ?? [])
    {
        if (!System.Net.IPAddress.TryParse(address, out var proxy))
        {
            throw new InvalidOperationException(
                $"Feedback:TrustedProxies contains an invalid IP address: {address}");
        }

        options.KnownProxies.Add(proxy);
    }
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("feedback", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new  FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));
});
builder.Services.AddSingleton(buildPolicy);
if (httpsRedirection.Enabled)
{
    builder.Services.AddHttpsRedirection(options =>
    {
        options.HttpsPort = httpsRedirection.HttpsPort;
    });
}

builder.Services.AddSingleton(new SteamApiOptions
{
    ApiKey = steamApiKey
});
builder.Services.AddHttpClient<SteamApiClient>(client =>
{
    client.BaseAddress = new Uri("https://api.steampowered.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
})
.RemoveAllLoggers();
builder.Services.AddHttpClient<ISteamStoreClient, SteamStoreClient>(client =>
{
    client.BaseAddress = new Uri("https://store.steampowered.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
})
.RemoveAllLoggers();
builder.Services.AddHttpClient("SteamLibraryImages", client =>
{
    client.BaseAddress = new Uri("https://cdn.cloudflare.steamstatic.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
})
.RemoveAllLoggers();
builder.Services.AddScoped<SteamAuthenticationManager>();
builder.Services.AddScoped<VotingManager>();
builder.Services.AddSingleton<MMMAtlas.AtlasEvents.AtlasEventBroadcaster>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme =
            AuthenticationSchemes.ApplicationCookie;
        options.DefaultSignInScheme =
            AuthenticationSchemes.ApplicationCookie;
        options.DefaultChallengeScheme =
            AuthenticationSchemes.ApplicationCookie;
    })
    .AddCookie(AuthenticationSchemes.ApplicationCookie, options =>
    {
        options.Cookie.Name = "mmm-atlas.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddSteam(options =>
    {
        options.SignInScheme = AuthenticationSchemes.ApplicationCookie;
        options.Events.OnTicketReceived = async context =>
        {
            var authenticationLogger = context.HttpContext.RequestServices
                .GetRequiredService<ILogger<SteamAuthenticationManager>>();
            var identifier = context.Principal?
                .FindFirstValue(ClaimTypes.NameIdentifier);
            var steamIdText = identifier?
                .TrimEnd('/')
                .Split('/')
                .LastOrDefault();

            if (!long.TryParse(steamIdText, out var steamId)
                || steamId <= 0)
            {
                authenticationLogger.LogWarning(
                    "Steam sign-in returned an invalid identifier. TraceId {TraceId}.",
                    context.HttpContext.TraceIdentifier);
                context.Fail("Steam did not return a valid Steam ID.");
                return;
            }

            var manager = context.HttpContext.RequestServices
                .GetRequiredService<SteamAuthenticationManager>();
            SteamSignInResult result;
            try
            {
                result = await manager.CompleteSignInAsync(
                    steamId,
                    context.HttpContext.RequestAborted);
            }
            catch (Exception exception)
            {
                authenticationLogger.LogError(
                    exception,
                    "Steam sign-in completion failed. TraceId {TraceId}.",
                    context.HttpContext.TraceIdentifier);
                throw;
            }

            authenticationLogger.LogInformation(
                "Steam authentication accepted for UserId {UserId}. "
                + "TraceId {TraceId}.",
                result.UserId,
                context.HttpContext.TraceIdentifier);

            var name = string.IsNullOrWhiteSpace(result.PersonaName)
                ? $"Steam {steamId}"
                : result.PersonaName;
            var identity = new ClaimsIdentity(
                [
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        result.UserId.ToString()),
                    new Claim(ClaimTypes.Name, name),
                    new Claim(SteamClaims.UserId, result.UserId.ToString()),
                    new Claim(SteamClaims.SteamId, steamId.ToString())
                ],
                AuthenticationSchemes.ApplicationCookie,
                ClaimTypes.Name,
                ClaimTypes.Role);
            context.Principal = new ClaimsPrincipal(identity);
        };
    });

var profilerEnabled = buildPolicy.TestFeaturesEnabled
    && builder.Configuration.GetValue<bool>("ApiProfiler:Enabled");
var databaseConnectionName = profilerEnabled
    ? "MMMAtlasProfiler"
    : buildPolicy.DatabaseConnectionName;
var connectionString =
    builder.Configuration.GetConnectionString(databaseConnectionName)
    ?? throw new InvalidOperationException(
        $"Connection string '{databaseConnectionName}' "
        + "is not configured.");

if (profilerEnabled)
{
    ProfilerDatabaseSeeder.EnsureDedicatedConnection(
        connectionString,
        builder.Configuration.GetConnectionString("MMMAtlasDevelopment"),
        builder.Configuration.GetConnectionString("MMMAtlasProduction"),
        builder.Environment.ContentRootPath);
}

var database = DatabaseConnection.Create(
    connectionString,
    builder.Environment.ContentRootPath);

builder.Services.AddSingleton(database);

var dataProtectionKeyDirectory = Path.Combine(
    builder.Environment.ContentRootPath,
    "App_Data",
    "DataProtection-Keys");

Directory.CreateDirectory(dataProtectionKeyDirectory);

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(
        new DirectoryInfo(dataProtectionKeyDirectory));

var app = builder.Build();

app.Logger.LogInformation(
    "MMM Atlas starting in {BuildMode} mode using database {DatabaseName}; "
    + "API profiler enabled: {ProfilerEnabled}.",
    buildPolicy.Mode,
    databaseConnectionName,
    profilerEnabled);

if (!buildPolicy.DebugEnabled)
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute(
        "/not-found",
        createScopeForStatusCodePages: true));
if (httpsRedirection.Enabled)
{
    app.UseHttpsRedirection();
}
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<OperationalRequestLoggingMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

var seedOnStartup = buildPolicy.UsesDevelopmentDatabase
    && !profilerEnabled
    && builder.Configuration.GetValue<bool>(
        "DevelopmentData:SeedOnStartup");
var databaseInitializationStarted = System.Diagnostics.Stopwatch
    .GetTimestamp();

try
{
    await DatabaseInitializer.InitializeAsync(
        database,
        seedOnStartup);
    app.Logger.LogInformation(
        "Database {DatabaseName} initialized in {ElapsedMilliseconds:F1} ms; "
        + "seed requested: {SeedOnStartup}.",
        databaseConnectionName,
        System.Diagnostics.Stopwatch.GetElapsedTime(
            databaseInitializationStarted).TotalMilliseconds,
        seedOnStartup);
}
catch (Exception exception)
{
    app.Logger.LogCritical(
        exception,
        "Database {DatabaseName} initialization failed after "
        + "{ElapsedMilliseconds:F1} ms; seed requested: {SeedOnStartup}.",
        databaseConnectionName,
        System.Diagnostics.Stopwatch.GetElapsedTime(
            databaseInitializationStarted).TotalMilliseconds,
        seedOnStartup);
    throw;
}

app.MapStaticAssets();
app.MapGameEndpoints();
app.MapAtlasEventEndpoints();
app.MapSteamAccountEndpoints();
app.MapVoteEndpoints();
app.MapFeedbackEndpoints();
app.MapProfilerEndpoints(buildPolicy, builder.Configuration);
app.MapControllers();
app.MapRazorPages()
    .WithStaticAssets();

app.Run();

public partial class Program;
