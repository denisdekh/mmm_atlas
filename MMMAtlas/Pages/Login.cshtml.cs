using AspNet.Security.OpenId.Steam;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MMMAtlas.Pages;

public sealed class LoginModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Dashboard");
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = Url.Page("/Dashboard") ?? "/dashboard"
        };

        return Challenge(
            properties,
            SteamAuthenticationDefaults.AuthenticationScheme);
    }
}
