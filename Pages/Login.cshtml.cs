using System.Security.Claims;
using Dapper;
using Ferreteria.Models;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// Ingreso a un negocio. Se llega desde la pantalla de bienvenida con ?negocio=tornilleria
// o ?negocio=ferreteria. Cada usuario solo puede entrar a su negocio; el administrador
// global puede entrar a los dos.
public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config)
    {
        _config = config;
    }

    [BindProperty(SupportsGet = true)]
    public string? Negocio { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string NombreUsuario { get; set; } = "";

    [BindProperty]
    public string Clave { get; set; } = "";

    public string? Mensaje { get; set; }

    public IActionResult OnGet()
    {
        if (!Services.Negocio.EsValido(Negocio))
            return RedirectToPage("/Bienvenida", new { ReturnUrl });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!Services.Negocio.EsValido(Negocio))
            return RedirectToPage("/Bienvenida");

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, nombre_usuario AS NombreUsuario, clave_hash AS ClaveHash, rol, negocio, activo AS Activo
            FROM usuarios
            WHERE lower(nombre_usuario) = lower(@Nombre)";

        var usuario = await conn.QuerySingleOrDefaultAsync<Usuario>(sql, new { Nombre = NombreUsuario.Trim() });

        if (usuario is null || !usuario.Activo || !BCrypt.Net.BCrypt.Verify(Clave, usuario.ClaveHash))
        {
            Mensaje = "Usuario o contraseña incorrectos.";
            return Page();
        }

        if (usuario.Rol != Services.Negocio.AdminGlobal && usuario.Negocio != Negocio)
        {
            Mensaje = $"Tu usuario es de {Services.Negocio.Nombre(usuario.Negocio)}. Vuelve atrás y entra por {Services.Negocio.Nombre(usuario.Negocio)}.";
            return Page();
        }

        await IniciarSesionAsync(HttpContext, usuario, Negocio!);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return LocalRedirect(ReturnUrl);
        return RedirectToPage("/Index");
    }

    // También la usa CambiarNegocio para que el administrador global pase de un negocio al otro.
    public static Task IniciarSesionAsync(HttpContext http, Usuario usuario, string negocio)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new(ClaimTypes.Name, usuario.NombreUsuario),
            // El administrador global entra como administrador del negocio elegido
            new(ClaimTypes.Role, usuario.Rol == Services.Negocio.AdminGlobal ? Services.Negocio.Admin : usuario.Rol),
            new(Services.Negocio.ClaimPerfil, usuario.Rol),
            new(Services.Negocio.ClaimNegocio, negocio)
        };

        var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identidad));
    }
}
