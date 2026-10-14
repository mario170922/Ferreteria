using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize(Roles = "admin")]
public class AjustarModel : PageModel
{
    private readonly IConfiguration _config;
    public AjustarModel(IConfiguration config) => _config = config;

    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Medida { get; set; }
    public int StockActual { get; set; }
    public string? Error { get; set; }
    public string? Mensaje { get; set; }

    [BindProperty] public int NuevoStock { get; set; }
    [BindProperty] public string Motivo { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Id = id;
        var ok = await CargarAsync();
        if (!ok) return RedirectToPage("/Productos");
        NuevoStock = StockActual;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        Id = id;

        if (NuevoStock < 0)
        {
            Error = "El stock no puede ser negativo.";
        }
        else if (string.IsNullOrWhiteSpace(Motivo))
        {
            Error = "Escribe el motivo del ajuste (por ejemplo: conteo físico, producto dañado).";
        }

        if (Error == null)
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                var actual = await conn.ExecuteScalarAsync<int?>(
                    "SELECT stock FROM productos WHERE id = @id FOR UPDATE",
                    new { id }, tx);

                if (actual == null)
                    throw new InvalidOperationException("El producto no existe.");

                var delta = NuevoStock - actual.Value;

                await conn.ExecuteAsync(
                    "UPDATE productos SET stock = @nuevo WHERE id = @id",
                    new { nuevo = NuevoStock, id }, tx);

                if (delta != 0)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
                        VALUES (@id, @delta, @motivo)",
                        new { id, delta, motivo = "Ajuste manual: " + Motivo.Trim() }, tx);
                }

                await tx.CommitAsync();
                TempData["Mensaje"] = "Inventario ajustado correctamente.";
                return RedirectToPage("/Productos");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Error = ex.Message;
            }
        }

        await CargarAsync();
        return Page();
    }

    private async Task<bool> CargarAsync()
    {
        using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var p = await conn.QuerySingleOrDefaultAsync<ProductoAjuste>(@"
            SELECT nombre AS Nombre, medida AS Medida, stock AS StockActual
            FROM productos WHERE id = @id",
            new { id = Id });

        if (p == null) return false;

        Nombre = p.Nombre;
        Medida = p.Medida;
        StockActual = p.StockActual;
        return true;
    }

    private class ProductoAjuste
    {
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int StockActual { get; set; }
    }
}