using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize(Roles = "admin")]
public class EditarModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _entorno;

    public EditarModel(IConfiguration config, IWebHostEnvironment entorno)
    {
        _config = config;
        _entorno = entorno;
    }

    [BindProperty]
    public ProductoForm Form { get; set; } = new();

    [BindProperty]
    public IFormFile? Imagen { get; set; }

    public string? ImagenActual { get; set; }
    public List<CategoriaOpcion> Categorias { get; set; } = new();
    public string? Mensaje { get; set; }

    public class ProductoForm
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string? CodigoBarras { get; set; }
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioCompra { get; set; }
        public int StockMinimo { get; set; }
        public List<int> CategoriaIds { get; set; } = new();
        public List<int> SubcategoriaIds { get; set; } = new();
    }

    public class CategoriaOpcion
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public List<SubcategoriaOpcion> Subcategorias { get; set; } = new();
    }

    public class SubcategoriaOpcion
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
    }

    private class ProductoFila
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string? CodigoBarras { get; set; }
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioCompra { get; set; }
        public int StockMinimo { get; set; }
        public string? Imagen { get; set; }
    }

    private class SubcategoriaFila
    {
        public int Id { get; set; }
        public int CategoriaId { get; set; }
        public string Nombre { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        var p = await conn.QuerySingleOrDefaultAsync<ProductoFila>(@"
            SELECT id, codigo, codigo_barras AS CodigoBarras, nombre, medida,
                   precio_compra AS PrecioCompra,
                   stock_minimo  AS StockMinimo,
                   imagen
            FROM productos
            WHERE id = @id AND negocio = @negocio", new { id, negocio = User.NegocioActual() });

        if (p is null)
            return RedirectToPage("/Productos");

        Form = new ProductoForm
        {
            Id = p.Id,
            Codigo = p.Codigo,
            CodigoBarras = p.CodigoBarras,
            Nombre = p.Nombre,
            Medida = p.Medida,
            PrecioCompra = p.PrecioCompra,
            StockMinimo = p.StockMinimo,
            CategoriaIds = (await conn.QueryAsync<int>(
                "SELECT categoria_id FROM producto_categorias WHERE producto_id = @id", new { id })).ToList(),
            SubcategoriaIds = (await conn.QueryAsync<int>(
                "SELECT subcategoria_id FROM producto_subcategorias WHERE producto_id = @id", new { id })).ToList()
        };

        ImagenActual = p.Imagen;
        await CargarCategoriasAsync(conn);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Form.Codigo = (Form.Codigo ?? "").Trim();
        Form.Nombre = (Form.Nombre ?? "").Trim();

        var errores = new List<string>();
        if (Form.Codigo.Length == 0)
            errores.Add("El código es obligatorio.");
        if (Form.Nombre.Length == 0)
            errores.Add("El nombre es obligatorio.");
        if (Form.PrecioCompra < 0)
            errores.Add("El precio de compra no puede ser negativo.");
        if (Form.StockMinimo < 0)
            errores.Add("El stock mínimo no puede ser negativo.");

        Form.CodigoBarras = CodigoBarras.Limpiar(Form.CodigoBarras);
        if (CodigoBarras.Validar(Form.CodigoBarras) is { } errorBarras)
            errores.Add(errorBarras);

        if (errores.Count > 0)
            return await MostrarConErrorAsync(string.Join(" ", errores));

        string? nuevaRuta = null;
        try
        {
            if (Imagen is { Length: > 0 })
                nuevaRuta = await ImagenService.GuardarAsync(Imagen, "productos", "producto", _entorno);

            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var anterior = await conn.ExecuteScalarAsync<string?>(
                "SELECT imagen FROM productos WHERE id = @Id", new { Form.Id }, tx);

            var negocio = User.NegocioActual();
            var esDelNegocio = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM productos WHERE id = @Id AND negocio = @negocio)",
                new { Form.Id, negocio }, tx);
            if (!esDelNegocio)
                throw new InvalidOperationException("Ese producto no es de este negocio.");

            // Si se borró el código de barras, se crea uno propio: todo producto debe tener uno.
            if (Form.CodigoBarras.Length == 0)
                Form.CodigoBarras = await CodigoBarras.GenerarAsync(conn, tx);

            await conn.ExecuteAsync(@"
                UPDATE productos
                SET codigo        = @Codigo,
                    codigo_barras = @CodigoBarras,
                    nombre        = @Nombre,
                    medida        = @Medida,
                    precio_compra = @PrecioCompra,
                    stock_minimo  = @StockMinimo,
                    imagen        = COALESCE(@nuevaRuta, imagen)
                WHERE id = @Id AND negocio = @negocio",
                new
                {
                    negocio,
                    Form.Id,
                    Form.Codigo,
                    Form.CodigoBarras,
                    Form.Nombre,
                    Form.Medida,
                    Form.PrecioCompra,
                    Form.StockMinimo,
                    nuevaRuta
                }, tx);

            // Categorías: las marcadas, más las que contienen las subcategorías marcadas.
            // Solo se aceptan categorías y subcategorías de este mismo negocio.
            var subIds = (await conn.QueryAsync<int>(@"
                SELECT s.id FROM subcategorias s JOIN categorias c ON c.id = s.categoria_id
                WHERE s.id = ANY(@ids) AND c.negocio = @negocio",
                new { ids = Form.SubcategoriaIds.Distinct().ToArray(), negocio }, tx)).ToArray();
            var catIds = Form.CategoriaIds.Distinct().ToList();

            if (subIds.Length > 0)
            {
                var catsDeSub = await conn.QueryAsync<int>(
                    "SELECT categoria_id FROM subcategorias WHERE id = ANY(@subIds)",
                    new { subIds }, tx);
                catIds.AddRange(catsDeSub);
            }
            catIds = (await conn.QueryAsync<int>(
                "SELECT id FROM categorias WHERE id = ANY(@ids) AND negocio = @negocio",
                new { ids = catIds.Distinct().ToArray(), negocio }, tx)).ToList();

            await conn.ExecuteAsync(
                "DELETE FROM producto_subcategorias WHERE producto_id = @Id", new { Form.Id }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM producto_categorias WHERE producto_id = @Id", new { Form.Id }, tx);

            foreach (var catId in catIds)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO producto_categorias (producto_id, categoria_id) VALUES (@pid, @catId)",
                    new { pid = Form.Id, catId }, tx);
            }

            foreach (var subId in subIds)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO producto_subcategorias (producto_id, subcategoria_id) VALUES (@pid, @subId)",
                    new { pid = Form.Id, subId }, tx);
            }

            await tx.CommitAsync();

            // Solo se borra la imagen anterior si se cargó una nueva
            if (nuevaRuta is not null)
                ImagenService.Eliminar(anterior, _entorno);

            TempData["Mensaje"] = "Producto guardado.";
            return RedirectToPage("/Productos");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            ImagenService.Eliminar(nuevaRuta, _entorno);
            return await MostrarConErrorAsync(ex.ConstraintName == "ux_productos_codigo_barras"
                ? "Ya existe otro producto con ese código de barras."
                : "Ya existe otro producto con ese código.");
        }
        catch (InvalidOperationException ex)
        {
            return await MostrarConErrorAsync(ex.Message);
        }
    }

    private async Task<IActionResult> MostrarConErrorAsync(string texto)
    {
        Mensaje = texto;

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        ImagenActual = await conn.ExecuteScalarAsync<string?>(
            "SELECT imagen FROM productos WHERE id = @Id", new { Form.Id });

        await CargarCategoriasAsync(conn);
        return Page();
    }

    private async Task CargarCategoriasAsync(NpgsqlConnection conn)
    {
        var categorias = (await conn.QueryAsync<CategoriaOpcion>(
            "SELECT id, nombre FROM categorias WHERE negocio = @negocio ORDER BY nombre",
            new { negocio = User.NegocioActual() })).AsList();

        var subcategorias = (await conn.QueryAsync<SubcategoriaFila>(@"
            SELECT s.id, s.categoria_id AS CategoriaId, s.nombre
            FROM subcategorias s
            JOIN categorias c ON c.id = s.categoria_id
            WHERE c.negocio = @negocio
            ORDER BY s.nombre", new { negocio = User.NegocioActual() })).AsList();

        foreach (var cat in categorias)
        {
            cat.Subcategorias = subcategorias
                .Where(s => s.CategoriaId == cat.Id)
                .Select(s => new SubcategoriaOpcion { Id = s.Id, Nombre = s.Nombre })
                .ToList();
        }

        Categorias = categorias;
    }
}