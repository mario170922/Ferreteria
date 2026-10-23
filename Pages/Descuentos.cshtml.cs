using System.Security.Claims;
using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize]
public class DescuentosModel : PageModel
{
    private readonly IConfiguration _config;

    public DescuentosModel(IConfiguration config)
    {
        _config = config;
    }

    [BindProperty]
    public List<FilaDescuento> Filas { get; set; } = new();

    public string? Mensaje { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!EsAdmin())
        {
            return Forbid();
        }

        await CargarAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!EsAdmin())
        {
            return Forbid();
        }

        foreach (var fila in Filas.Where(f => f.Rol != "admin"))
        {
            if (fila.DescuentoPct < 0 || fila.DescuentoPct > 100)
            {
                Error = $"El descuento de {fila.NombreUsuario} debe estar entre 0 y 100.";
                return Page();
            }
        }

        var connStr = _config.GetConnectionString("Ferreteria");
        await using var con = new NpgsqlConnection(connStr);
        await con.OpenAsync();

        foreach (var fila in Filas)
        {
            await con.ExecuteAsync(
                "UPDATE usuarios SET descuento_pct = @pct WHERE id = @id AND rol = 'vendedor' AND negocio = @negocio",
                new { pct = fila.DescuentoPct, id = fila.Id, negocio = User.NegocioActual() });
        }

        Mensaje = "Descuentos guardados. Se aplicarán a los pedidos nuevos.";
        await CargarAsync();
        return Page();
    }

    private bool EsAdmin()
    {
        return User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");
    }

    private async Task CargarAsync()
    {
        var connStr = _config.GetConnectionString("Ferreteria");
        await using var con = new NpgsqlConnection(connStr);
        await con.OpenAsync();

        var lista = await con.QueryAsync<FilaDescuento>(@"
            SELECT id AS Id,
                   nombre_usuario AS NombreUsuario,
                   activo AS Activo,
                   descuento_pct AS DescuentoPct,
                   rol AS Rol
            FROM usuarios
            WHERE negocio = @negocio AND rol IN ('vendedor', 'admin')
            ORDER BY rol DESC, nombre_usuario", new { negocio = User.NegocioActual() });

        Filas = lista.ToList();
    }

    public class FilaDescuento
    {
        public int Id { get; set; }
        public string NombreUsuario { get; set; } = "";
        public bool Activo { get; set; }
        public decimal DescuentoPct { get; set; }
        public string Rol { get; set; } = "vendedor";
    }
}