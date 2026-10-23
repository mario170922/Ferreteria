using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// Etiquetas con código de barras para pegar en los productos o en los estantes.
// Se eligen los productos y cuántas etiquetas de cada uno, y se imprime una hoja.
[Authorize(Roles = "admin")]
public class EtiquetasModel : PageModel
{
    private readonly IConfiguration _config;

    public EtiquetasModel(IConfiguration config) => _config = config;

    public List<ProductoEtiqueta> Productos { get; set; } = new();
    public List<ProductoEtiqueta> Hoja { get; set; } = new();   // una entrada por etiqueta a imprimir
    public bool Imprimir { get; set; }
    public bool ConPrecio { get; set; } = true;

    public async Task OnGetAsync()
    {
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        Productos = (await con.QueryAsync<ProductoEtiqueta>(@"
            SELECT id AS Id, codigo AS Codigo, codigo_barras AS CodigoBarras, nombre AS Nombre,
                   medida AS Medida, precio_venta AS PrecioVenta, precio_por AS PrecioPor, stock AS Stock
            FROM productos
            WHERE negocio = @negocio AND codigo_barras IS NOT NULL
            ORDER BY nombre, medida", new { negocio = User.NegocioActual() })).ToList();

        // Las cantidades llegan como ?c_12=3&c_40=1 (producto 12: 3 etiquetas)
        Imprimir = Request.Query.ContainsKey("imprimir");
        ConPrecio = !Request.Query.ContainsKey("imprimir") || Request.Query["precio"] == "1";
        foreach (var p in Productos)
        {
            if (int.TryParse(Request.Query[$"c_{p.Id}"], out var n) && n > 0)
            {
                p.Copias = Math.Min(n, 200);
                Hoja.AddRange(Enumerable.Repeat(p, p.Copias));
            }
        }
        if (Hoja.Count == 0) Imprimir = false;
    }

    public class ProductoEtiqueta
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string CodigoBarras { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioVenta { get; set; }
        public int PrecioPor { get; set; } = 1;
        public int Stock { get; set; }
        public int Copias { get; set; }
    }
}
