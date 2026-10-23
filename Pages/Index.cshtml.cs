using System.Globalization;
using System.Security.Claims;
using Dapper;
using Ferreteria.Services;
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

    // Datos de las gráficas (últimos 7 días de ventas, estados y más vendidos)
    public List<VentaDia> Ventas7Dias { get; set; } = new();
    public List<ConteoEstado> Estados { get; set; } = new();
    public List<MasVendido> MasVendidos { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToPage("/Bienvenida");
        }

        // El conductor trabaja desde su pantalla de entregas
        if (User.IsInRole(Ferreteria.Services.Negocio.Conductor))
            return RedirectToPage("/Entregas");

        NombreUsuario = User.Identity.Name ?? "";
        EsAdmin = User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");

        var cultura = new CultureInfo("es-CO");
        var hoy = DateTime.Now.ToString("dddd d 'de' MMMM", cultura);
        Fecha = char.ToUpper(hoy[0]) + hoy[1..];

        // Si la base no responde, el inicio se muestra igual, solo sin cifras.
        try
        {
            await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

            // Solo cifras del negocio actual. El vendedor solo ve las de sus propios pedidos.
            Datos = await con.QueryFirstAsync<Resumen>(@"
                SELECT
                    (SELECT count(*) FROM pedidos p
                      WHERE p.negocio = @negocio AND p.estado = 'pendiente'
                        AND (@esAdmin OR p.vendedor_id = u.id))                                  AS Pendientes,
                    (SELECT count(*) FROM pedidos p
                      WHERE p.negocio = @negocio AND p.fecha::date = current_date
                        AND (@esAdmin OR p.vendedor_id = u.id))                                  AS PedidosHoy,
                    (SELECT count(*) FROM productos
                      WHERE negocio = @negocio AND stock_minimo > 0 AND stock <= stock_minimo)   AS StockBajo,
                    (SELECT count(*) FROM clientes)                                               AS Clientes
                FROM (SELECT (SELECT id FROM usuarios WHERE lower(nombre_usuario) = lower(@nombre)) AS id) u",
                new { esAdmin = User.VeTodosLosPedidos(), nombre = NombreUsuario, negocio = User.NegocioActual() });

            await CargarGraficasAsync(con);
        }
        catch (NpgsqlException)
        {
            Datos = new Resumen();
        }

        return Page();
    }

    // Las gráficas usan los mismos filtros: el vendedor solo ve sus pedidos.
    // Los pedidos cancelados y no entregados no cuentan como venta.
    private async Task CargarGraficasAsync(NpgsqlConnection con)
    {
        var filtro = new { esAdmin = User.VeTodosLosPedidos(), nombre = NombreUsuario, negocio = User.NegocioActual() };
        const string soloMios = @"p.negocio = @negocio AND (@esAdmin OR p.vendedor_id =
            (SELECT id FROM usuarios WHERE lower(nombre_usuario) = lower(@nombre)))";

        var ventas = (await con.QueryAsync<VentaDia>($@"
            SELECT d::date AS Dia,
                   COALESCE(SUM(p.total), 0) AS Total,
                   COUNT(p.id) AS Pedidos
            FROM generate_series(current_date - 6, current_date, interval '1 day') d
            LEFT JOIN pedidos p ON p.fecha::date = d::date
                               AND p.estado NOT IN ('cancelado', 'no_entregado')
                               AND {soloMios}
            GROUP BY d
            ORDER BY d", filtro)).ToList();
        Ventas7Dias = ventas;

        Estados = (await con.QueryAsync<ConteoEstado>($@"
            SELECT p.estado AS Estado, COUNT(*) AS Cantidad
            FROM pedidos p
            WHERE p.fecha >= current_date - 29 AND {soloMios}
            GROUP BY p.estado", filtro)).ToList();

        MasVendidos = (await con.QueryAsync<MasVendido>($@"
            SELECT pr.nombre AS Nombre, pr.medida AS Medida, SUM(pi.cantidad) AS Cantidad
            FROM pedido_items pi
            JOIN pedidos p    ON p.id = pi.pedido_id
            JOIN productos pr ON pr.id = pi.producto_id
            WHERE p.fecha >= current_date - 29
              AND p.estado NOT IN ('cancelado', 'no_entregado')
              AND {soloMios}
            GROUP BY pr.id, pr.nombre, pr.medida
            ORDER BY SUM(pi.cantidad) DESC
            LIMIT 5", filtro)).ToList();
    }

    public class VentaDia
    {
        public DateTime Dia { get; set; }
        public decimal Total { get; set; }
        public long Pedidos { get; set; }
    }

    public class ConteoEstado
    {
        public string Estado { get; set; } = "";
        public long Cantidad { get; set; }
    }

    public class MasVendido
    {
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public long Cantidad { get; set; }
    }

    public class Resumen
    {
        public long Pendientes { get; set; }
        public long PedidosHoy { get; set; }
        public long StockBajo { get; set; }
        public long Clientes { get; set; }
    }
}
