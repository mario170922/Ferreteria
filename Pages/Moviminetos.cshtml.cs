using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize]
public class MovimientosModel : PageModel
{
    private readonly IConfiguration _config;
    public MovimientosModel(IConfiguration config) => _config = config;

    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Medida { get; set; }
    public int Stock { get; set; }
    public List<MovimientoFila> Historial { get; set; } = new();
    public string? Error { get; set; }
    public string? Mensaje { get; set; }

    [BindProperty] public string Tipo { get; set; } = "entrada";
    [BindProperty] public int Cantidad { get; set; }
    [BindProperty] public string Motivo { get; set; } = "";

    public class MovimientoFila
    {
        public int Cantidad { get; set; }
        public string? Motivo { get; set; }
        public DateTime Fecha { get; set; }
    }

    private class ProductoBase
    {
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int Stock { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Id = id;
        if (!await CargarAsync(id)) return RedirectToPage("/Productos");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        Id = id;

        if (Cantidad <= 0)
        {
            Error = "La cantidad debe ser mayor a cero.";
        }

        if (Error == null)
        {
            var delta = Tipo == "salida" ? -Cantidad : Cantidad;

            using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                var filas = await conn.ExecuteAsync(@"
                    UPDATE productos SET stock = stock + @delta
                    WHERE id = @id AND stock + @delta >= 0",
                    new { delta, id }, tx);

                if (filas == 0)
                    throw new InvalidOperationException("No hay stock suficiente para esa salida.");

                await conn.ExecuteAsync(@"
                    INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
                    VALUES (@id, @delta, @motivo)",
                    new { id, delta, motivo = Motivo.Trim() }, tx);

                await tx.CommitAsync();
                Mensaje = "Movimiento guardado.";
                Cantidad = 0;
                Motivo = "";
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Error = ex.Message;
            }
        }

        await CargarAsync(id);
        return Page();
    }

    private async Task<bool> CargarAsync(int id)
    {
        using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var p = await conn.QuerySingleOrDefaultAsync<ProductoBase>(@"
            SELECT nombre AS Nombre, medida AS Medida, stock AS Stock
            FROM productos WHERE id = @id", new { id });

        if (p == null) return false;

        Nombre = p.Nombre;
        Medida = p.Medida;
        Stock = p.Stock;

        // Solo el administrador ve el historial
        if (User.IsInRole("admin"))
        {
            Historial = (await conn.QueryAsync<MovimientoFila>(@"
                SELECT cantidad AS Cantidad, motivo AS Motivo, fecha AS Fecha
                FROM movimientos_inventario
                WHERE producto_id = @id
                ORDER BY fecha DESC
                LIMIT 50", new { id })).ToList();
        }

        return true;
    }
}