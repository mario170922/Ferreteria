using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize(Roles = "admin")]
public class NuevoModel : PageModel
{
    private readonly IConfiguration _config;
    public NuevoModel(IConfiguration config) => _config = config;

    [BindProperty] public ProductoNuevo Producto { get; set; } = new();
    public string? Error { get; set; }

    public class ProductoNuevo
    {
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioCompra { get; set; }
        public int Stock { get; set; }
        public int StockMinimo { get; set; }
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        Producto.Codigo = (Producto.Codigo ?? "").Trim();
        Producto.Nombre = (Producto.Nombre ?? "").Trim();
        Producto.Medida = string.IsNullOrWhiteSpace(Producto.Medida) ? null : Producto.Medida.Trim();

        // Mismas reglas que al editar, más los límites de la tabla productos.
        if (Producto.Codigo.Length == 0 || Producto.Nombre.Length == 0)
            Error = "El código y el nombre son obligatorios.";
        else if (Producto.Codigo.Length > 30)
            Error = "El código es muy largo (máximo 30 letras).";
        else if (Producto.Nombre.Length > 150)
            Error = "El nombre es muy largo (máximo 150 letras).";
        else if ((Producto.Medida ?? "").Length > 50)
            Error = "La medida es muy larga (máximo 50 letras).";
        else if (Producto.PrecioCompra < 0)
            Error = "El precio de compra no puede ser negativo.";
        else if (Producto.PrecioCompra > 9_999_999_999m)
            Error = "El precio de compra es demasiado alto.";
        else if (Producto.Stock < 0 || Producto.StockMinimo < 0)
            Error = "El stock no puede ser negativo.";

        if (Error != null)
            return Page();

        try
        {
            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            int id = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO productos (codigo, nombre, medida, precio_compra, stock, stock_minimo)
                VALUES (@Codigo, @Nombre, @Medida, @PrecioCompra, @Stock, @StockMinimo)
                RETURNING id",
                Producto, tx);

            // El stock inicial queda en el historial, igual que cualquier otra entrada.
            if (Producto.Stock > 0)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
                    VALUES (@id, @cantidad, 'Stock inicial')",
                    new { id, cantidad = Producto.Stock }, tx);
            }

            await tx.CommitAsync();

            TempData["Mensaje"] = $"Producto {Producto.Nombre} creado. Toca Editar en su tarjeta para agregarle foto y categorías.";
            return RedirectToPage("/Productos");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            Error = "Ya existe un producto con ese código.";
            return Page();
        }
    }
}