using Dapper;
using Ferreteria.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize]
public class ProductosModel : PageModel
{
    private readonly IConfiguration _config;

    public ProductosModel(IConfiguration config)
    {
        _config = config;
    }

    public List<Producto> Lista { get; set; } = new();
    public string? Mensaje { get; set; }

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"]?.ToString();

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, codigo, nombre, medida,
                   precio_compra AS PrecioCompra,
                   precio_venta  AS PrecioVenta,
                   stock, stock_minimo AS StockMinimo
            FROM productos
            ORDER BY nombre, codigo";

        Lista = (await conn.QueryAsync<Producto>(sql)).AsList();
    }

    // Llamado desde JavaScript mientras se escribe en el buscador
    public async Task<IActionResult> OnGetBuscarAsync(string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new JsonResult(Array.Empty<Producto>());

        var patron = $"%{q.Trim()}%";

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, codigo, nombre, medida,
                   precio_compra AS PrecioCompra,
                   precio_venta  AS PrecioVenta,
                   stock, stock_minimo AS StockMinimo
            FROM productos
            WHERE codigo ILIKE @patron
               OR nombre ILIKE @patron
               OR medida ILIKE @patron
            ORDER BY codigo
            LIMIT 8";

        var resultados = await conn.QueryAsync<Producto>(sql, new { patron });
        return new JsonResult(resultados.ToList());
    }

    public async Task<IActionResult> OnPostEliminarAsync(int id)
    {
        if (!User.IsInRole("admin"))
        {
            TempData["Mensaje"] = "Solo el administrador puede eliminar productos.";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        try
        {
            await conn.ExecuteAsync("DELETE FROM productos WHERE id = @id", new { id });
            TempData["Mensaje"] = "Producto eliminado.";
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            TempData["Mensaje"] = "No se puede eliminar: el producto tiene movimientos de inventario.";
        }

        return RedirectToPage();
    }
}