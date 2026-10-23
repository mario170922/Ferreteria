using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ferreteria.Pages;

// Aquí llega quien abre una página de administrador sin serlo.
[Authorize]
public class SinPermisoModel : PageModel
{
    public void OnGet()
    {
    }
}
