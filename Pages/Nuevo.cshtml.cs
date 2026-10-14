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
        if (string.IsNullOrWhiteSpace(Producto.Codigo) || string.IsNullOrWhiteSpace(Producto.Nombre))
        {
            Error = "El código y el nombre son obligatorios.";
            return Page();
        }

        try
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.ExecuteAsync(@"
                INSERT INTO productos (codigo, nombre, medida, precio_compra, stock, stock_minimo)
                VALUES (@Codigo, @Nombre, @Medida, @PrecioCompra, @Stock, @StockMinimo)",
                Producto);

            return RedirectToPage("/Productos");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            Error = "Ya existe un producto con ese código.";
            return Page();
        }
    }
}