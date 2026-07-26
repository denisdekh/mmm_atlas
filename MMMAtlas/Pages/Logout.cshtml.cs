using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MMMAtlas.Steam;

namespace MMMAtlas.Pages;

public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(
            AuthenticationSchemes.ApplicationCookie);
        return RedirectToPage("/Index");
    }
}
