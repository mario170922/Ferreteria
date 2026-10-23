using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Compras;

// Compras del negocio actual, cargadas desde la factura electrónica del proveedor.
[Authorize(Roles = "admin")]
public class IndexModel : PageModel
{
    private readonly IConfiguration _config;

    public IndexModel(IConfiguration config) => _config = config;

    public List<CompraFila> Lista { get; set; } = new();
    public string? Mensaje { get; set; }

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"] as string;
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        Lista = (await con.QueryAsync<CompraFila>(@"
            SELECT c.id AS Id, c.fecha AS Fecha, c.numero_factura AS Numero, pr.nombre AS Proveedor,
                   c.total AS Total,
                   (SELECT count(*) FROM compra_items i WHERE i.compra_id = c.id) AS Productos
            FROM compras c
            JOIN proveedores pr ON pr.id = c.proveedor_id
            WHERE c.negocio = @negocio
            ORDER BY c.fecha DESC, c.id DESC
            LIMIT 200", new { negocio = User.NegocioActual() })).ToList();
    }

    public class CompraFila
    {
        public int Id { get; set; }
        public DateTime Fecha { get; set; }
        public string Numero { get; set; } = "";
        public string Proveedor { get; set; } = "";
        public decimal Total { get; set; }
        public long Productos { get; set; }
    }
}
