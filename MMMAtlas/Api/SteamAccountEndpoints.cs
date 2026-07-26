using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MMMAtlas.Steam;

namespace MMMAtlas.Api;

public static class SteamAccountEndpoints
{
    public static IEndpointRouteBuilder MapSteamAccountEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var steamData = endpoints.MapGroup("/api/me/steam-data")
            .RequireAuthorization()
            .WithTags("Steam account");

        steamData.MapPost("/erase", EraseStoredDataAsync);
        steamData.MapPost("/consent", SetConsentAsync);

        return endpoints;
    }

    private static async Task<IResult> EraseStoredDataAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        SteamAuthenticationManager manager,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        await manager.EraseStoredDataAsync(
            GetUserId(httpContext.User),
            cancellationToken);

        return WantsHtml(httpContext.Request)
            ? Results.Redirect("/dashboard?notice=erased")
            : Results.NoContent();
    }

    private static async Task<IResult> SetConsentAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        SteamAuthenticationManager manager,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var enabled = form["enabled"].Any(value =>
            string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase));

        await manager.SetDataCollectionEnabledAsync(
            GetUserId(httpContext.User),
            enabled,
            cancellationToken);

        return WantsHtml(httpContext.Request)
            ? Results.Redirect("/dashboard?notice=preference-saved")
            : Results.NoContent();
    }

    private static long GetUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(SteamClaims.UserId);
        return long.TryParse(value, out var userId)
            ? userId
            : throw new InvalidOperationException(
                "The authentication cookie does not contain a valid user ID.");
    }

    private static bool WantsHtml(HttpRequest request) =>
        request.HasFormContentType
        || request.GetTypedHeaders().Accept?.Any(
            value => string.Equals(
                value.MediaType.Value,
                "text/html",
                StringComparison.OrdinalIgnoreCase)) == true;
}
