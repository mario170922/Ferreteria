using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// "Qué comprar": productos en el mínimo o por debajo, cuánto pedir para quedar bien
// surtido y a qué proveedor se le compró la última vez, agrupado por proveedor.
[Authorize(Roles = "admin")]
public class ReposicionModel : PageModel
{
    private readonly IConfiguration _config;

    public ReposicionModel(IConfiguration config) => _config = config;

    public List<Fila> Filas { get; set; } = new();

    public async Task OnGetAsync()
    {
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        Filas = (await con.QueryAsync<Fila>(@"
            SELECT p.id AS Id, p.codigo AS Codigo, p.nombre AS Nombre, p.medida AS Medida,
                   p.stock AS Stock, p.stock_minimo AS StockMinimo, round(p.precio_compra / p.precio_por, 2) AS PrecioCompra,
                   COALESCE((SELECT SUM(pi.cantidad) FROM pedido_items pi JOIN pedidos pe ON pe.id = pi.pedido_id
                             WHERE pi.producto_id = p.id AND pe.fecha >= now() - interval '30 days'
                               AND pe.estado NOT IN ('cancelado', 'no_entregado')), 0) AS Vendidas30,
                   ult.proveedor AS Proveedor, ult.fecha AS UltimaCompra
            FROM productos p
            LEFT JOIN LATERAL (
                SELECT pv.nombre AS proveedor, c.fecha
                FROM compra_items i JOIN compras c ON c.id = i.compra_id JOIN proveedores pv ON pv.id = c.proveedor_id
                WHERE i.producto_id = p.id
                ORDER BY c.fecha DESC, c.id DESC LIMIT 1) ult ON true
            WHERE p.negocio = @negocio AND p.stock_minimo > 0 AND p.stock <= p.stock_minimo
            ORDER BY ult.proveedor NULLS LAST, p.nombre", new { negocio = User.NegocioActual() })).ToList();
    }

    public class Fila
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int Stock { get; set; }
        public int StockMinimo { get; set; }
        public decimal PrecioCompra { get; set; }
        public long Vendidas30 { get; set; }
        public string? Proveedor { get; set; }
        public DateTime? UltimaCompra { get; set; }

        // Pedir lo necesario para llegar a 3 veces el mínimo (la barra de stock "llena"),
        // o lo vendido en los últimos 30 días si es más.
        public int Sugerido => Math.Max(StockMinimo * 3, (int)Vendidas30) - Stock;
        public decimal CostoEstimado => Sugerido * PrecioCompra;
    }
}
