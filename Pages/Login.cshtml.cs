using System.Security.Claims;
using Dapper;
using Ferreteria.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config)
    {
        _config = config;
    }

    [BindProperty]
    public string NombreUsuario { get; set; } = "";

    [BindProperty]
    public string Clave { get; set; } = "";

    public string? Mensaje { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, nombre_usuario AS NombreUsuario, clave_hash AS ClaveHash, rol, activo AS Activo
            FROM usuarios
            WHERE lower(nombre_usuario) = lower(@Nombre)";

        var usuario = await conn.QuerySingleOrDefaultAsync<Usuario>(sql, new { Nombre = NombreUsuario.Trim() });

        if (usuario is null || !usuario.Activo || !BCrypt.Net.BCrypt.Verify(Clave, usuario.ClaveHash))
        {
            Mensaje = "Usuario o contraseña incorrectos.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new(ClaimTypes.Name, usuario.NombreUsuario),
            new(ClaimTypes.Role, usuario.Rol)
        };

        var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identidad));

        return RedirectToPage("/Index");
    }
}