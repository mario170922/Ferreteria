using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ferreteria.Pages;

// Primera pantalla del sitio: la persona elige si entra a Tornillería o a Ferretería.
public class BienvenidaModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        // Quien ya ingresó va directo al inicio de su negocio
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Index");
        return Page();
    }
}
