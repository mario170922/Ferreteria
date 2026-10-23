using Dapper;
using Ferreteria.Services;
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
        public string? CodigoBarras { get; set; }
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioCompra { get; set; }
        public decimal MargenPct { get; set; } = 100;
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
        else if (Producto.MargenPct < 0 || Producto.MargenPct > 1000)
            Error = "La ganancia debe estar entre 0 % y 1000 %.";

        Producto.CodigoBarras = CodigoBarras.Limpiar(Producto.CodigoBarras);
        Error ??= CodigoBarras.Validar(Producto.CodigoBarras);

        if (Error != null)
            return Page();

        try
        {
            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            if (Producto.CodigoBarras.Length == 0)
                Producto.CodigoBarras = await CodigoBarras.GenerarAsync(conn, tx);

            int id = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO productos (negocio, codigo, codigo_barras, nombre, medida, precio_compra, margen_pct, stock, stock_minimo)
                VALUES (@negocio, @Codigo, @CodigoBarras, @Nombre, @Medida, @PrecioCompra, @MargenPct, @Stock, @StockMinimo)
                RETURNING id",
                new
                {
                    negocio = User.NegocioActual(),
                    Producto.Codigo,
                    Producto.CodigoBarras,
                    Producto.Nombre,
                    Producto.Medida,
                    Producto.PrecioCompra,
                    Producto.MargenPct,
                    Producto.Stock,
                    Producto.StockMinimo
                }, tx);

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
            Error = ex.ConstraintName == "ux_productos_codigo_barras"
                ? "Ya existe otro producto con ese código de barras."
                : "Ya existe un producto con ese código.";
            return Page();
        }
    }
}