using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

[Authorize(Roles = "admin")]
public class CategoriasModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _entorno;

    public CategoriasModel(IConfiguration config, IWebHostEnvironment entorno)
    {
        _config = config;
        _entorno = entorno;
    }

    public List<CategoriaVista> Categorias { get; set; } = new();
    public string? Mensaje { get; set; }
    public bool EsError { get; set; }

    public class CategoriaVista
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string? Imagen { get; set; }
        public List<SubcategoriaVista> Subcategorias { get; set; } = new();
        public List<ProductoVista> Productos { get; set; } = new();
    }

    public class SubcategoriaVista
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
    }

    public class ProductoVista
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public string Subcategorias { get; set; } = "";
    }

    // Filas auxiliares para Dapper
    private class SubcategoriaFila
    {
        public int Id { get; set; }
        public int CategoriaId { get; set; }
        public string Nombre { get; set; } = "";
    }

    private class ProductoFila
    {
        public int CategoriaId { get; set; }
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
    }

    private class ProductoSubFila
    {
        public int ProductoId { get; set; }
        public int CategoriaId { get; set; }
        public string Nombre { get; set; } = "";
    }

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"]?.ToString() ?? TempData["Error"]?.ToString();
        EsError = TempData["Error"] is not null;

        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await CargarAsync(conn);
    }

    public async Task<IActionResult> OnPostCrearCategoriaAsync(string nombre, IFormFile? imagen)
    {
        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0)
            return MensajeError("El nombre de la categoría es obligatorio.");

        string? ruta = null;
        try
        {
            if (imagen is { Length: > 0 })
                ruta = await ImagenService.GuardarAsync(imagen, "categorias", "categoria", _entorno);

            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.ExecuteAsync(
                "INSERT INTO categorias (nombre, imagen) VALUES (@nombre, @ruta)",
                new { nombre, ruta });

            return MensajeOk("Categoría creada.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            ImagenService.Eliminar(ruta, _entorno);
            return MensajeError("Ya existe una categoría con ese nombre.");
        }
        catch (InvalidOperationException ex)
        {
            return MensajeError(ex.Message);
        }
    }

    public async Task<IActionResult> OnPostCambiarImagenAsync(int id, IFormFile? imagen)
    {
        if (imagen is not { Length: > 0 })
            return MensajeError("Seleccione una imagen.");

        try
        {
            var nueva = await ImagenService.GuardarAsync(imagen, "categorias", "categoria", _entorno);

            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            var anterior = await conn.ExecuteScalarAsync<string?>(
                "SELECT imagen FROM categorias WHERE id = @id", new { id });

            await conn.ExecuteAsync(
                "UPDATE categorias SET imagen = @nueva WHERE id = @id",
                new { nueva, id });

            ImagenService.Eliminar(anterior, _entorno);
            return MensajeOk("Imagen actualizada.");
        }
        catch (InvalidOperationException ex)
        {
            return MensajeError(ex.Message);
        }
    }

    public async Task<IActionResult> OnPostCrearSubcategoriaAsync(int categoriaId, string nombre)
    {
        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0)
            return MensajeError("El nombre de la subcategoría es obligatorio.");

        try
        {
            await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await conn.ExecuteAsync(
                "INSERT INTO subcategorias (categoria_id, nombre) VALUES (@categoriaId, @nombre)",
                new { categoriaId, nombre });

            return MensajeOk("Subcategoría agregada.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return MensajeError("Ya existe una subcategoría con ese nombre en esta categoría.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            return MensajeError("La categoría seleccionada ya no existe.");
        }
    }

    // Al eliminar una subcategoría, sus productos pasan a la categoría que la contenía.
    public async Task<IActionResult> OnPostEliminarSubcategoriaAsync(int subcategoriaId)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var categoriaId = await conn.ExecuteScalarAsync<int?>(
            "SELECT categoria_id FROM subcategorias WHERE id = @subcategoriaId",
            new { subcategoriaId }, tx);

        if (categoriaId is null)
        {
            await tx.RollbackAsync();
            return MensajeError("La subcategoría no existe.");
        }

        await conn.ExecuteAsync(@"
            INSERT INTO producto_categorias (producto_id, categoria_id)
            SELECT ps.producto_id, @categoriaId
            FROM producto_subcategorias ps
            WHERE ps.subcategoria_id = @subcategoriaId
            ON CONFLICT DO NOTHING",
            new { categoriaId = categoriaId.Value, subcategoriaId }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM subcategorias WHERE id = @subcategoriaId",
            new { subcategoriaId }, tx);

        await tx.CommitAsync();
        return MensajeOk("Subcategoría eliminada. Sus productos quedaron en la categoría.");
    }

    private async Task CargarAsync(NpgsqlConnection conn)
    {
        var categorias = (await conn.QueryAsync<CategoriaVista>(
            "SELECT id, nombre, imagen FROM categorias ORDER BY nombre")).AsList();

        var subcategorias = (await conn.QueryAsync<SubcategoriaFila>(@"
            SELECT id, categoria_id AS CategoriaId, nombre
            FROM subcategorias
            ORDER BY nombre")).AsList();

        var productos = (await conn.QueryAsync<ProductoFila>(@"
            SELECT pc.categoria_id AS CategoriaId, p.id, p.codigo, p.nombre, p.medida
            FROM producto_categorias pc
            JOIN productos p ON p.id = pc.producto_id
            ORDER BY p.nombre, p.codigo")).AsList();

        var productoSubs = (await conn.QueryAsync<ProductoSubFila>(@"
            SELECT ps.producto_id AS ProductoId, s.categoria_id AS CategoriaId, s.nombre
            FROM producto_subcategorias ps
            JOIN subcategorias s ON s.id = ps.subcategoria_id
            ORDER BY s.nombre")).AsList();

        foreach (var cat in categorias)
        {
            cat.Subcategorias = subcategorias
                .Where(s => s.CategoriaId == cat.Id)
                .Select(s => new SubcategoriaVista { Id = s.Id, Nombre = s.Nombre })
                .ToList();

            cat.Productos = productos
                .Where(p => p.CategoriaId == cat.Id)
                .Select(p => new ProductoVista
                {
                    Id = p.Id,
                    Codigo = p.Codigo,
                    Nombre = p.Nombre,
                    Medida = p.Medida,
                    Subcategorias = string.Join(", ", productoSubs
                        .Where(ps => ps.ProductoId == p.Id && ps.CategoriaId == cat.Id)
                        .Select(ps => ps.Nombre))
                })
                .ToList();
        }

        Categorias = categorias;
    }

    private RedirectToPageResult MensajeOk(string texto)
    {
        TempData["Mensaje"] = texto;
        return RedirectToPage();
    }

    private RedirectToPageResult MensajeError(string texto)
    {
        TempData["Error"] = texto;
        return RedirectToPage();
    }
}