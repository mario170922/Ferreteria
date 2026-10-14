using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ferreteria.Pages;

public class IndexModel : PageModel
{
    public bool EsAdmin { get; set; }
    public string NombreUsuario { get; set; } = "";

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToPage("/Login");
        }

        NombreUsuario = User.Identity.Name ?? "";
        EsAdmin = User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");

        return Page();
    }
}