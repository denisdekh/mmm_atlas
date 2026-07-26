using AspNet.Security.OpenId.Steam;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MMMAtlas.Steam;

namespace MMMAtlas.Pages;

public sealed class LoginModel : PageModel
{
    [BindProperty]
    public bool CollectSteamData { get; set; }

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
        properties.Items[AuthenticationSchemes.CollectionConsentProperty] =
            CollectSteamData.ToString();

        return Challenge(
            properties,
            SteamAuthenticationDefaults.AuthenticationScheme);
    }
}
