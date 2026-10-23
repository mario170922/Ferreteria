using Dapper;
using Ferreteria.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

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

    [BindProperty]
    public string NombreUsuario { get; set; } = "";

    [BindProperty]
    public string Clave { get; set; } = "";

    [BindProperty]
    public string Rol { get; set; } = "vendedor";

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"]?.ToString();
        await CargarAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var nombre = NombreUsuario.Trim();

        if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(Clave))
        {
            Mensaje = "Escribe el usuario y la contraseña.";
            EsError = true;
            await CargarAsync();
            return Page();
        }

        if (Rol != "admin" && Rol != "vendedor")
            Rol = "vendedor";

        var hash = BCrypt.Net.BCrypt.HashPassword(Clave);

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        // Para entrar no importan las mayúsculas, así que "Pedro" y "pedro" serían el mismo usuario.
        bool repetido = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM usuarios WHERE lower(nombre_usuario) = lower(@nombre))",
            new { nombre });

        if (repetido)
        {
            Mensaje = "Ya existe un usuario con ese nombre.";
            EsError = true;
            await CargarAsync();
            return Page();
        }

        try
        {
            await conn.ExecuteAsync(
                "INSERT INTO usuarios (nombre_usuario, clave_hash, rol) VALUES (@nombre, @hash, @Rol)",
                new { nombre, hash, Rol });

            TempData["Mensaje"] = $"Usuario '{nombre}' creado.";
            return RedirectToPage();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            Mensaje = "Ya existe un usuario con ese nombre.";
            EsError = true;
            await CargarAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCambiarActivoAsync(int id)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        // El administrador principal no se puede desactivar desde aquí
        await conn.ExecuteAsync(
            "UPDATE usuarios SET activo = NOT activo WHERE id = @id AND lower(nombre_usuario) <> 'admin'",
            new { id });

        TempData["Mensaje"] = "Estado del usuario actualizado.";
        return RedirectToPage();
    }

    private async Task CargarAsync()
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, nombre_usuario AS NombreUsuario, rol, activo AS Activo
            FROM usuarios
            ORDER BY nombre_usuario";

        Lista = (await conn.QueryAsync<Usuario>(sql)).AsList();
    }
}