using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"] as string;
        Error = TempData["Error"] as string;
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

    // Anular una factura cargada por error: se resta del inventario lo que esa factura sumó
    // y la compra se borra (deja de contar como egreso y se puede volver a cargar bien).
    // En Movimientos queda el registro de la entrada y de la anulación.
    // No se puede si parte de esa mercancía ya se vendió (el stock no alcanza para restarla).
    public async Task<IActionResult> OnPostAnularAsync(int id)
    {
        var negocio = User.NegocioActual();
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        var compra = await con.QueryFirstOrDefaultAsync<(string Numero, string Proveedor)?>(@"
            SELECT c.numero_factura, p.nombre FROM compras c JOIN proveedores p ON p.id = c.proveedor_id
            WHERE c.id = @id AND c.negocio = @negocio FOR UPDATE OF c", new { id, negocio }, tx);
        if (compra is null)
        {
            TempData["Error"] = "Esa compra no existe en este negocio.";
            return RedirectToPage();
        }

        // Unidades que sumó cada producto en esta factura
        var porProducto = (await con.QueryAsync<(int ProductoId, string Nombre, int Stock, int Unidades)>(@"
            SELECT pr.id, pr.nombre, pr.stock, SUM(round(i.cantidad * i.unidades_por))::int
            FROM compra_items i JOIN productos pr ON pr.id = i.producto_id
            WHERE i.compra_id = @id
            GROUP BY pr.id, pr.nombre, pr.stock", new { id }, tx)).ToList();

        var faltan = porProducto.Where(p => p.Stock < p.Unidades).ToList();
        if (faltan.Count > 0)
        {
            await tx.RollbackAsync();
            TempData["Error"] = "No se puede anular: ya se vendió parte de la mercancía. " + string.Join("; ",
                faltan.Select(p => $"{p.Nombre}: entraron {p.Unidades} y quedan {p.Stock}"))
                + ". Corrige el stock en Movimientos.";
            return RedirectToPage();
        }

        var motivo = $"Anulación: factura {compra.Value.Numero} de {compra.Value.Proveedor}";
        foreach (var p in porProducto)
        {
            await con.ExecuteAsync("UPDATE productos SET stock = stock - @u WHERE id = @pid",
                new { u = p.Unidades, pid = p.ProductoId }, tx);
            await con.ExecuteAsync(
                "INSERT INTO movimientos_inventario (producto_id, cantidad, motivo) VALUES (@pid, @c, @motivo)",
                new { pid = p.ProductoId, c = -p.Unidades, motivo }, tx);
        }

        await con.ExecuteAsync("DELETE FROM compras WHERE id = @id", new { id }, tx);
        await tx.CommitAsync();

        TempData["Mensaje"] = $"Factura {compra.Value.Numero} anulada: se restaron del inventario las unidades que había sumado. Ya la puedes volver a cargar.";
        return RedirectToPage();
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
