using System.Globalization;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

public class IndexModel : PageModel
{
    private readonly IConfiguration _config;

    public IndexModel(IConfiguration config)
    {
        _config = config;
    }

    public bool EsAdmin { get; set; }
    public string NombreUsuario { get; set; } = "";
    public string Fecha { get; set; } = "";

    // Cifras rápidas que se muestran arriba
    public Resumen Datos { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToPage("/Login");
        }

        NombreUsuario = User.Identity.Name ?? "";
        EsAdmin = User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");

        var cultura = new CultureInfo("es-CO");
        var hoy = DateTime.Now.ToString("dddd d 'de' MMMM", cultura);
        Fecha = char.ToUpper(hoy[0]) + hoy[1..];

        // Si la base no responde, el inicio se muestra igual, solo sin cifras.
        try
        {
            await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

            // El vendedor solo ve las cifras de sus propios pedidos.
            Datos = await con.QueryFirstAsync<Resumen>(@"
                SELECT
                    (SELECT count(*) FROM pedidos p
                      WHERE p.estado = 'pendiente' AND (@esAdmin OR p.vendedor_id = u.id))      AS Pendientes,
                    (SELECT count(*) FROM pedidos p
                      WHERE p.fecha::date = current_date AND (@esAdmin OR p.vendedor_id = u.id)) AS PedidosHoy,
                    (SELECT count(*) FROM productos WHERE stock_minimo > 0 AND stock <= stock_minimo) AS StockBajo,
                    (SELECT count(*) FROM clientes)                                               AS Clientes
                FROM (SELECT (SELECT id FROM usuarios WHERE nombre_usuario = @nombre) AS id) u",
                new { esAdmin = EsAdmin, nombre = NombreUsuario });
        }
        catch (NpgsqlException)
        {
            Datos = new Resumen();
        }

        return Page();
    }

    public class Resumen
    {
        public long Pendientes { get; set; }
        public long PedidosHoy { get; set; }
        public long StockBajo { get; set; }
        public long Clientes { get; set; }
    }
}
