using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MMMAtlas.Steam;

namespace MMMAtlas.Pages;

[Authorize]
public sealed class DashboardModel(
    SteamAuthenticationManager authenticationManager) : PageModel
{
    public SteamDashboard? Dashboard { get; private set; }

    [FromQuery]
    public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(
        CancellationToken cancellationToken)
    {
        var userIdValue = User.FindFirstValue(SteamClaims.UserId);
        if (!long.TryParse(userIdValue, out var userId))
        {
            return Challenge();
        }

        Dashboard = await authenticationManager.GetDashboardAsync(
            userId,
            cancellationToken);
        return Page();
    }
}
