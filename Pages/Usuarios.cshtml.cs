using Dapper;
using Ferreteria.Models;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// Usuarios y perfiles. Cada negocio tiene sus propios usuarios:
// - La lista muestra los usuarios del negocio en el que se está trabajando, más los
//   administradores globales (que entran a los dos).
// - Los usuarios nuevos quedan en el negocio actual. Para crear uno de Ferretería,
//   el administrador global primero pasa a Ferretería con el botón de la barra.
// - Solo el administrador global puede crear otros administradores globales.
[Authorize(Roles = "admin")]
public class UsuariosModel : PageModel
{
    private readonly IConfiguration _config;

    public UsuariosModel(IConfiguration config)
    {
        _config = config;
    }

    public List<Usuario> Lista { get; set; } = new();
    public string? Mensaje { get; set; }
    public bool EsError { get; set; }
    public bool EsAdminGlobal => User.EsAdminGlobal();

    [BindProperty]
    public string NombreUsuario { get; set; } = "";

    [BindProperty]
    public string Clave { get; set; } = "";

    [BindProperty]
    public string Rol { get; set; } = Negocio.Vendedor;

    // Perfiles que puede crear quien está en la página
    public string[] PerfilesPermitidos => EsAdminGlobal
        ? Negocio.Perfiles
        : new[] { Negocio.Vendedor, Negocio.Cajero, Negocio.Conductor, Negocio.Admin };

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"]?.ToString();
        await CargarAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var nombre = NombreUsuario.Trim();

        if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(Clave))
            return await ErrorAsync("Escribe el usuario y la contraseña.");

        if (!PerfilesPermitidos.Contains(Rol))
            return await ErrorAsync("Elige un perfil de la lista.");

        // El administrador global no es de un negocio; los demás quedan en el negocio actual.
        string? negocio = Rol == Negocio.AdminGlobal ? null : User.NegocioActual();

        var hash = BCrypt.Net.BCrypt.HashPassword(Clave);

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        // Para entrar no importan las mayúsculas, así que "Pedro" y "pedro" serían el mismo usuario.
        bool repetido = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM usuarios WHERE lower(nombre_usuario) = lower(@nombre))",
            new { nombre });

        if (repetido)
            return await ErrorAsync("Ya existe un usuario con ese nombre.");

        try
        {
            await conn.ExecuteAsync(
                "INSERT INTO usuarios (nombre_usuario, clave_hash, rol, negocio) VALUES (@nombre, @hash, @Rol, @negocio)",
                new { nombre, hash, Rol, negocio });

            TempData["Mensaje"] = $"Usuario '{nombre}' creado como {Negocio.NombrePerfil(Rol).ToLower()}"
                + (negocio is null ? "." : $" de {Negocio.Nombre(negocio)}.");
            return RedirectToPage();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return await ErrorAsync("Ya existe un usuario con ese nombre.");
        }
    }

    public async Task<IActionResult> OnPostCambiarActivoAsync(int id)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        // El administrador principal no se puede desactivar desde aquí.
        // El administrador de un negocio solo cambia usuarios de su negocio.
        await conn.ExecuteAsync(@"
            UPDATE usuarios SET activo = NOT activo
            WHERE id = @id AND lower(nombre_usuario) <> 'admin'
              AND (negocio = @negocio OR (@global AND rol = 'admin_global'))",
            new { id, global = EsAdminGlobal, negocio = User.NegocioActual() });

        TempData["Mensaje"] = "Estado del usuario actualizado.";
        return RedirectToPage();
    }

    private async Task<IActionResult> ErrorAsync(string texto)
    {
        Mensaje = texto;
        EsError = true;
        await CargarAsync();
        return Page();
    }

    private async Task CargarAsync()
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, nombre_usuario AS NombreUsuario, rol, negocio, activo AS Activo
            FROM usuarios
            WHERE negocio = @negocio OR rol = 'admin_global'
            ORDER BY negocio NULLS FIRST, nombre_usuario";

        Lista = (await conn.QueryAsync<Usuario>(sql, new { negocio = User.NegocioActual() })).AsList();
    }
}
