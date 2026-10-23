using Dapper;
using Ferreteria.Models;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// Solo el administrador global puede pasar de un negocio al otro sin volver a ingresar.
[Authorize]
public class CambiarNegocioModel : PageModel
{
    private readonly IConfiguration _config;

    public CambiarNegocioModel(IConfiguration config) => _config = config;

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync(string negocio)
    {
        if (!User.EsAdminGlobal() || !Negocio.EsValido(negocio))
            return RedirectToPage("/SinPermiso");

        // Se vuelve a leer el usuario de la base, por si lo desactivaron
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var usuario = await conn.QueryFirstOrDefaultAsync<Usuario>(@"
            SELECT id, nombre_usuario AS NombreUsuario, rol, negocio, activo AS Activo
            FROM usuarios WHERE lower(nombre_usuario) = lower(@n)",
            new { n = User.Identity?.Name ?? "" });

        if (usuario is null || !usuario.Activo || usuario.Rol != Negocio.AdminGlobal)
            return RedirectToPage("/Salir");

        await LoginModel.IniciarSesionAsync(HttpContext, usuario, negocio);
        return RedirectToPage("/Index");
    }
}
