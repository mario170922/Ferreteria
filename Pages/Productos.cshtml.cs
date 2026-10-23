using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize]
public class ProductosModel : PageModel
{
    public const int TamanoPagina = 24;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _entorno;

    public ProductosModel(IConfiguration config, IWebHostEnvironment entorno)
    {
        _config = config;
        _entorno = entorno;
    }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? Categoria { get; set; }
    [BindProperty(SupportsGet = true)] public int? Sub { get; set; }
    [BindProperty(SupportsGet = true)] public bool Bajo { get; set; }
    [BindProperty(SupportsGet = true)] public int Pagina { get; set; } = 1;

    public List<ProductoTarjeta> Lista { get; set; } = new();
    public List<CategoriaChip> Categorias { get; set; } = new();
    public List<SubcategoriaChip> Subcategorias { get; set; } = new();

    public int TotalGeneral { get; set; }
    public int TotalBajo { get; set; }
    public int TotalFiltrado { get; set; }
    public int TotalPaginas { get; set; } = 1;
    public string? Mensaje { get; set; }
    public bool EsError { get; set; }

    public bool HayFiltros =>
        !string.IsNullOrWhiteSpace(Q) || Categoria is not null || Sub is not null || Bajo;

    public class ProductoTarjeta
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioCompra { get; set; }
        public decimal PrecioVenta { get; set; }
        public int Stock { get; set; }
        public int StockMinimo { get; set; }
        public string? Imagen { get; set; }
        public string Categorias { get; set; } = "";

        public bool StockBajo => StockMinimo > 0 && Stock <= StockMinimo;
    }

    public class CategoriaChip
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string? Imagen { get; set; }
        public int Cantidad { get; set; }
    }

    public class SubcategoriaChip
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public int Cantidad { get; set; }
    }

    public string ObtenerRutaImagen(string? imagen, string carpeta)
    {
        if (string.IsNullOrEmpty(imagen)) return "";
        if (imagen.Contains('/')) return "/" + imagen.TrimStart('/'); return $"/img/{carpeta}/{imagen}";
    }

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"]?.ToString();
        EsError = TempData["EsError"] is true;

        Q = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();
        if (Categoria is null) Sub = null;
        if (Pagina < 1) Pagina = 1;

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        TotalGeneral = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM productos");
        TotalBajo = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM productos WHERE stock_minimo > 0 AND stock <= stock_minimo");

        Categorias = (await conn.QueryAsync<CategoriaChip>(@"
            SELECT c.id AS Id, c.nombre AS Nombre, c.imagen AS Imagen,
                   COUNT(pc.producto_id)::int AS Cantidad
            FROM categorias c
            LEFT JOIN producto_categorias pc ON pc.categoria_id = c.id
            GROUP BY c.id, c.nombre, c.imagen
            ORDER BY c.nombre")).AsList();

        if (Categoria is not null && Categorias.All(c => c.Id != Categoria))
        {
            Categoria = null;
            Sub = null;
        }

        if (Categoria is not null)
        {
            Subcategorias = (await conn.QueryAsync<SubcategoriaChip>(@"
                SELECT s.id AS Id, s.nombre AS Nombre,
                       COUNT(ps.producto_id)::int AS Cantidad
                FROM subcategorias s
                LEFT JOIN producto_subcategorias ps ON ps.subcategoria_id = s.id
                WHERE s.categoria_id = @categoria
                GROUP BY s.id, s.nombre
                ORDER BY s.nombre", new { categoria = Categoria })).AsList();

            if (Sub is not null && Subcategorias.All(s => s.Id != Sub))
                Sub = null;
        }

        var condiciones = new List<string>();
        var parametros = new DynamicParameters();

        if (Q is not null)
        {
            var patron = "%" + Q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            condiciones.Add("(p.codigo ILIKE @patron OR p.nombre ILIKE @patron OR p.medida ILIKE @patron)");
            parametros.Add("patron", patron);
        }

        if (Categoria is not null)
        {
            condiciones.Add(@"EXISTS (SELECT 1 FROM producto_categorias pc
                                      WHERE pc.producto_id = p.id AND pc.categoria_id = @categoria)");
            parametros.Add("categoria", Categoria);
        }

        if (Sub is not null)
        {
            condiciones.Add(@"EXISTS (SELECT 1 FROM producto_subcategorias ps
                                      WHERE ps.producto_id = p.id AND ps.subcategoria_id = @sub)");
            parametros.Add("sub", Sub);
        }

        if (Bajo)
            condiciones.Add("(p.stock_minimo > 0 AND p.stock <= p.stock_minimo)");

        var donde = condiciones.Count > 0 ? "WHERE " + string.Join(" AND ", condiciones) : "";

        TotalFiltrado = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM productos p {donde}", parametros);

        TotalPaginas = Math.Max(1, (int)Math.Ceiling(TotalFiltrado / (double)TamanoPagina));
        if (Pagina > TotalPaginas) Pagina = TotalPaginas;

        parametros.Add("tam", TamanoPagina);
        parametros.Add("salto", (Pagina - 1) * TamanoPagina);

        var sql = $@"
            SELECT p.id AS Id, p.codigo AS Codigo, p.nombre AS Nombre, p.medida AS Medida,
                   p.precio_compra AS PrecioCompra,
                   p.precio_venta  AS PrecioVenta,
                   p.stock AS Stock, p.stock_minimo AS StockMinimo,
                   p.imagen AS Imagen,
                   COALESCE((SELECT string_agg(c.nombre, ', ' ORDER BY c.nombre)
                             FROM producto_categorias pc
                             JOIN categorias c ON c.id = pc.categoria_id
                             WHERE pc.producto_id = p.id), '') AS Categorias
            FROM productos p
            {donde}
            ORDER BY p.nombre, p.codigo
            LIMIT @tam OFFSET @salto";

        Lista = (await conn.QueryAsync<ProductoTarjeta>(sql, parametros)).AsList();
    }

    public string Enlace(int? categoria, int? sub, bool bajo, int pagina = 1)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(Q)) partes.Add("q=" + Uri.EscapeDataString(Q));
        if (categoria is not null) partes.Add("categoria=" + categoria);
        if (sub is not null) partes.Add("sub=" + sub);
        if (bajo) partes.Add("bajo=true");
        if (pagina > 1) partes.Add("pagina=" + pagina);

        return "/Productos" + (partes.Count > 0 ? "?" + string.Join("&", partes) : "");
    }

    public string EnlaceActual() => Enlace(Categoria, Sub, Bajo, Pagina);

    public async Task<IActionResult> OnPostEliminarAsync(int id, string? retorno)
    {
        if (!User.IsInRole("admin"))
        {
            TempData["Mensaje"] = "Solo el administrador puede eliminar productos.";
            TempData["EsError"] = true;
            return VolverALista(retorno);
        }

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        try
        {
            var imagen = await conn.ExecuteScalarAsync<string?>(
                "SELECT imagen FROM productos WHERE id = @id", new { id });

            var filas = await conn.ExecuteAsync("DELETE FROM productos WHERE id = @id", new { id });

            if (filas > 0)
            {
                ImagenService.Eliminar(imagen, _entorno);
                TempData["Mensaje"] = "Producto eliminado.";
            }
            else
            {
                TempData["Mensaje"] = "El producto ya no existe.";
                TempData["EsError"] = true;
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            TempData["Mensaje"] = "No se puede eliminar: el producto ya tiene movimientos o pedidos registrados. Si ya no se vende, déjalo con stock 0.";
            TempData["EsError"] = true;
        }

        return VolverALista(retorno);
    }

    private IActionResult VolverALista(string? retorno)
    {
        if (!string.IsNullOrEmpty(retorno) && retorno.StartsWith("/Productos") && Url.IsLocalUrl(retorno))
            return LocalRedirect(retorno);

        return RedirectToPage();
    }
}