using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize(Roles = "admin")]
public class VerCategoriaModel : PageModel
{
    private readonly IConfiguration _config;

    public VerCategoriaModel(IConfiguration config)
    {
        _config = config;
    }

    public CategoriaDetalle Categoria { get; set; } = new();
    public List<SubcategoriaDetalle> Subcategorias { get; set; } = new();
    public List<ProductoDetalle> Productos { get; set; } = new();
    public string? Mensaje { get; set; }
    public bool EsError { get; set; }

    public class CategoriaDetalle
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string? Imagen { get; set; }
    }

    public class SubcategoriaDetalle
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public int Cantidad { get; set; }
    }

    public class ProductoDetalle
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public string? Imagen { get; set; }
        public string SubIds { get; set; } = "";
        public string SubNombres { get; set; } = "";

        // Texto en minúsculas para el buscador de la página
        public string Texto => $"{Nombre} {Codigo} {Medida}".ToLowerInvariant();
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Mensaje = TempData["Mensaje"]?.ToString() ?? TempData["Error"]?.ToString();
        EsError = TempData["Error"] is not null;

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        var categoria = await conn.QuerySingleOrDefaultAsync<CategoriaDetalle>(
            "SELECT id AS Id, nombre AS Nombre, imagen AS Imagen FROM categorias WHERE id = @id AND negocio = @negocio",
            new { id, negocio = User.NegocioActual() });

        if (categoria is null)
            return RedirectToPage("/Categorias");

        Categoria = categoria;
        await CargarAsync(conn, id);
        return Page();
    }

    public async Task<IActionResult> OnPostCrearSubcategoriaAsync(int categoriaId, string nombre)
    {
        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0)
            return Volver(categoriaId, "El nombre de la subcategoría es obligatorio.", error: true);

        try
        {
            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.ExecuteAsync(
                @"INSERT INTO subcategorias (categoria_id, nombre)
                  SELECT @categoriaId, @nombre
                  WHERE EXISTS (SELECT 1 FROM categorias WHERE id = @categoriaId AND negocio = @negocio)",
                new { categoriaId, nombre, negocio = User.NegocioActual() });

            return Volver(categoriaId, "Subcategoría agregada.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Volver(categoriaId, "Ya existe una subcategoría con ese nombre.", error: true);
        }
    }

    // Al eliminar una subcategoría, sus productos pasan a la categoría que la contenía.
    public async Task<IActionResult> OnPostEliminarSubcategoriaAsync(int categoriaId, int subcategoriaId)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var categoriaReal = await conn.ExecuteScalarAsync<int?>(
            @"SELECT s.categoria_id FROM subcategorias s JOIN categorias c ON c.id = s.categoria_id
              WHERE s.id = @subcategoriaId AND c.negocio = @negocio",
            new { subcategoriaId, negocio = User.NegocioActual() }, tx);

        if (categoriaReal is null)
        {
            await tx.RollbackAsync();
            return Volver(categoriaId, "La subcategoría no existe.", error: true);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO producto_categorias (producto_id, categoria_id)
            SELECT ps.producto_id, @categoriaReal
            FROM producto_subcategorias ps
            WHERE ps.subcategoria_id = @subcategoriaId
            ON CONFLICT DO NOTHING",
            new { categoriaReal = categoriaReal.Value, subcategoriaId }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM subcategorias WHERE id = @subcategoriaId",
            new { subcategoriaId }, tx);

        await tx.CommitAsync();
        return Volver(categoriaReal.Value, "Subcategoría eliminada. Sus productos quedaron en la categoría.");
    }

    private async Task CargarAsync(NpgsqlConnection conn, int categoriaId)
    {
        Subcategorias = (await conn.QueryAsync<SubcategoriaDetalle>(@"
            SELECT s.id AS Id,
                   s.nombre AS Nombre,
                   (SELECT COUNT(*) FROM producto_subcategorias ps WHERE ps.subcategoria_id = s.id)::int AS Cantidad
            FROM subcategorias s
            WHERE s.categoria_id = @categoriaId
            ORDER BY s.nombre", new { categoriaId })).AsList();

        Productos = (await conn.QueryAsync<ProductoDetalle>(@"
            SELECT p.id AS Id,
                   p.codigo AS Codigo,
                   p.nombre AS Nombre,
                   p.medida AS Medida,
                   p.imagen AS Imagen,
                   COALESCE((SELECT string_agg(s.id::text, ',' ORDER BY s.nombre)
                             FROM producto_subcategorias ps
                             JOIN subcategorias s ON s.id = ps.subcategoria_id
                             WHERE ps.producto_id = p.id AND s.categoria_id = @categoriaId), '') AS SubIds,
                   COALESCE((SELECT string_agg(s.nombre, ', ' ORDER BY s.nombre)
                             FROM producto_subcategorias ps
                             JOIN subcategorias s ON s.id = ps.subcategoria_id
                             WHERE ps.producto_id = p.id AND s.categoria_id = @categoriaId), '') AS SubNombres
            FROM producto_categorias pc
            JOIN productos p ON p.id = pc.producto_id
            WHERE pc.categoria_id = @categoriaId
            ORDER BY p.nombre, p.codigo", new { categoriaId })).AsList();
    }

    private RedirectToPageResult Volver(int id, string texto, bool error = false)
    {
        if (error)
            TempData["Error"] = texto;
        else
            TempData["Mensaje"] = texto;

        return RedirectToPage("/Categorias/Ver", new { id });
    }
}