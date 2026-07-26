using System.Security.Claims;
using AspNet.Security.OpenId.Steam;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using MMMAtlas.Api;
using MMMAtlas.Data;
using MMMAtlas.Steam;
using MMMAtlas.Voting;

var builder = WebApplication.CreateBuilder(args);

var steamApiKey = builder.Configuration["Steam:ApiKey"]
    ?? throw new InvalidOperationException("Steam:ApiKey is not configured.");

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddAuthorization();

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
builder.Services.AddScoped<SteamAuthenticationManager>();
builder.Services.AddScoped<VotingManager>();

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
            var identifier = context.Principal?
                .FindFirstValue(ClaimTypes.NameIdentifier);
            var steamIdText = identifier?
                .TrimEnd('/')
                .Split('/')
                .LastOrDefault();

            if (!long.TryParse(steamIdText, out var steamId)
                || steamId <= 0)
            {
                context.Fail("Steam did not return a valid Steam ID.");
                return;
            }

            var collectionConsent =
                context.Properties?.Items.TryGetValue(
                    AuthenticationSchemes.CollectionConsentProperty,
                    out var consentValue) == true
                && string.Equals(
                    consentValue,
                    bool.TrueString,
                    StringComparison.OrdinalIgnoreCase);

            var manager = context.HttpContext.RequestServices
                .GetRequiredService<SteamAuthenticationManager>();
            var result = await manager.CompleteSignInAsync(
                steamId,
                collectionConsent,
                context.HttpContext.RequestAborted);

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

var connectionString =
    builder.Configuration.GetConnectionString("MMMAtlas")
    ?? throw new InvalidOperationException(
        "Connection string 'MMMAtlas' is not configured.");

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute(
        "/not-found",
        createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    await DevelopmentDatabase.InitializeAsync(database);
}

app.MapStaticAssets();
app.MapGameEndpoints();
app.MapSteamAccountEndpoints();
app.MapVoteEndpoints();
app.MapControllers();
app.MapRazorPages()
    .WithStaticAssets();

app.Run();

public partial class Program;
