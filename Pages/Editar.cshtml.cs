using Dapper;
using Ferreteria.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize(Roles = "admin")]
public class EditarModel : PageModel
{
    private readonly IConfiguration _config;

    public EditarModel(IConfiguration config)
    {
        _config = config;
    }

    [BindProperty]
    public Producto Producto { get; set; } = new();

    public string? Mensaje { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            SELECT id, codigo, nombre, medida,
                   precio_compra AS PrecioCompra,
                   precio_venta  AS PrecioVenta,
                   stock, stock_minimo AS StockMinimo
            FROM productos
            WHERE id = @id";

        var p = await conn.QuerySingleOrDefaultAsync<Producto>(sql, new { id });

        if (p is null)
            return RedirectToPage("/Productos");

        Producto = p;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Producto.Codigo))
            ModelState.AddModelError("Producto.Codigo", "El código es obligatorio.");

        if (string.IsNullOrWhiteSpace(Producto.Nombre))
            ModelState.AddModelError("Producto.Nombre", "El nombre es obligatorio.");

        if (!ModelState.IsValid)
            return Page();

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        const string sql = @"
            UPDATE productos
            SET codigo        = @Codigo,
                nombre        = @Nombre,
                medida        = @Medida,
                precio_compra = @PrecioCompra,
                stock_minimo  = @StockMinimo
            WHERE id = @Id";

        try
        {
            await conn.ExecuteAsync(sql, Producto);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            Mensaje = "Ya existe otro producto con ese código.";
            return Page();
        }

        return RedirectToPage("/Productos");
    }
}