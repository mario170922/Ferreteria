using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Compras;

// Cargar una factura de compra en dos pasos:
// 1. "Leer": se sube el .zip (o .xml) del correo del proveedor y se muestran sus productos,
//    cada uno con el producto nuestro que le corresponde (por código de barras o porque
//    ya se había usado antes ese código del proveedor).
// 2. "Guardar": la persona revisa, corrige lo que haga falta y confirma. Se suma al
//    inventario, se actualiza el precio de compra y la compra queda como egreso.
// El archivo se guarda en App_Data/facturas para poder consultarlo después.
[Authorize(Roles = "admin")]
[RequestSizeLimit(20_000_000)]
public class CargarModel : PageModel
{
    public const int CrearNuevo = 0;
    public const int NoSumar = -1;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _entorno;

    public CargarModel(IConfiguration config, IWebHostEnvironment entorno)
    {
        _config = config;
        _entorno = entorno;
    }

    public FacturaElectronica.Factura? Factura { get; set; }
    public List<ProductoOpcion> Productos { get; set; } = new();
    public string? Error { get; set; }

    [BindProperty] public string? Archivo { get; set; }          // nombre del archivo guardado en el paso 1
    [BindProperty] public List<LineaForm> Lineas { get; set; } = new();

    public class LineaForm
    {
        public int ProductoId { get; set; }
        public int UnidadesPor { get; set; } = 1;
        public string? CodigoNuevo { get; set; }
        public string? NombreNuevo { get; set; }
    }

    public class ProductoOpcion
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public string? CodigoBarras { get; set; }
        public string Texto => $"{Nombre}{(string.IsNullOrWhiteSpace(Medida) ? "" : " · " + Medida)} ({Codigo})";
    }

    private string Carpeta => Path.Combine(_entorno.ContentRootPath, "App_Data", "facturas");

    public void OnGet() { }

    // ---------- Paso 1: leer el archivo ----------
    public async Task<IActionResult> OnPostLeerAsync(IFormFile? factura)
    {
        if (factura is null || factura.Length == 0)
        {
            Error = "Elige el archivo .zip (o .xml) de la factura.";
            return Page();
        }

        var ext = Path.GetExtension(factura.FileName).ToLowerInvariant();
        if (ext is not (".zip" or ".xml"))
        {
            Error = "El archivo debe ser el .zip o el .xml que llegó en el correo. El PDF solo sirve para leer.";
            return Page();
        }

        Directory.CreateDirectory(Carpeta);
        Archivo = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{ext}";
        await using (var destino = System.IO.File.Create(Path.Combine(Carpeta, Archivo)))
            await factura.CopyToAsync(destino);

        try
        {
            Factura = LeerGuardado(Archivo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            System.IO.File.Delete(Path.Combine(Carpeta, Archivo));
            Error = ex is InvalidOperationException ? ex.Message : "El archivo no es una factura electrónica válida.";
            return Page();
        }

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var negocio = User.NegocioActual();

        if (await YaCargadaAsync(con, Factura) is { } aviso)
        {
            Error = aviso;
            Factura = null;
            return Page();
        }

        await CargarProductosAsync(con, negocio);
        Lineas = await SugerenciasAsync(con, negocio, Factura);
        return Page();
    }

    // ---------- Paso 2: guardar la compra ----------
    public async Task<IActionResult> OnPostGuardarAsync()
    {
        if (string.IsNullOrEmpty(Archivo) || Archivo.Contains('/') || Archivo.Contains('\\') || Archivo.Contains(".."))
            return RedirectToPage();

        try
        {
            Factura = LeerGuardado(Archivo);
        }
        catch (Exception)
        {
            Error = "No se encontró el archivo de la factura. Vuelve a cargarlo.";
            return Page();
        }

        var negocio = User.NegocioActual();
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await con.OpenAsync();

        if (Lineas.Count != Factura.Lineas.Count)
        {
            Error = "Los productos no coinciden con la factura. Vuelve a cargarla.";
            await CargarProductosAsync(con, negocio);
            return Page();
        }

        await using var tx = await con.BeginTransactionAsync();
        try
        {
            if (await YaCargadaAsync(con, Factura, tx) is { } aviso)
                throw new InvalidOperationException(aviso);

            var proveedorId = await con.ExecuteScalarAsync<int>(@"
                INSERT INTO proveedores (nit, nombre) VALUES (@nit, @nombre)
                ON CONFLICT (nit) DO UPDATE SET nombre = EXCLUDED.nombre
                RETURNING id", new { nit = Nit(Factura), nombre = Factura.ProveedorNombre }, tx);

            var usuarioId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var u) ? u : (int?)null;

            var compraId = await con.ExecuteScalarAsync<int>(@"
                INSERT INTO compras (negocio, proveedor_id, numero_factura, cufe, fecha, subtotal, impuestos, total, usuario_id)
                VALUES (@negocio, @proveedorId, @Numero, @Cufe, @Fecha, @Subtotal, @Impuestos, @Total, @usuarioId)
                RETURNING id",
                new { negocio, proveedorId, Factura.Numero, Factura.Cufe, Factura.Fecha, Factura.Subtotal, Factura.Impuestos, Factura.Total, usuarioId }, tx);

            var sumados = 0;
            var creados = 0;
            for (var i = 0; i < Factura.Lineas.Count; i++)
            {
                var linea = Factura.Lineas[i];
                var form = Lineas[i];
                var unidadesPor = Math.Max(1, form.UnidadesPor);
                int? productoId = null;

                if (form.ProductoId != NoSumar)
                {
                    var unidades = linea.Cantidad * unidadesPor;
                    if (unidades != Math.Floor(unidades))
                        throw new InvalidOperationException($"Línea {linea.Numero}: la cantidad ({unidades:0.###}) no da unidades enteras. Revisa \"unidades por cada una\".");
                    var cantidad = (int)unidades;
                    // Costo de cada unidad con impuestos incluidos: lo que realmente se pagó
                    var costoUnidad = cantidad > 0 ? Math.Round(linea.ValorTotal / cantidad, 2) : 0;

                    if (form.ProductoId == CrearNuevo)
                    {
                        productoId = await CrearProductoAsync(con, tx, negocio, linea, form, costoUnidad);
                        creados++;
                    }
                    else
                    {
                        productoId = await con.ExecuteScalarAsync<int?>(
                            "SELECT id FROM productos WHERE id = @id AND negocio = @negocio",
                            new { id = form.ProductoId, negocio }, tx)
                            ?? throw new InvalidOperationException($"Línea {linea.Numero}: el producto elegido no existe en este negocio.");
                    }

                    await con.ExecuteAsync(
                        "UPDATE productos SET stock = stock + @cantidad, precio_compra = @costoUnidad WHERE id = @productoId",
                        new { cantidad, costoUnidad, productoId }, tx);

                    await con.ExecuteAsync(@"
                        INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
                        VALUES (@productoId, @cantidad, @motivo)",
                        new { productoId, cantidad, motivo = $"Compra: factura {Factura.Numero} de {Factura.ProveedorNombre}" }, tx);

                    // Se recuerda para la próxima factura de este proveedor
                    if (linea.CodigoProveedor.Length > 0)
                    {
                        await con.ExecuteAsync(@"
                            DELETE FROM producto_codigos_proveedor pc
                            USING productos p
                            WHERE pc.producto_id = p.id AND p.negocio = @negocio
                              AND pc.proveedor_id = @proveedorId AND pc.codigo_proveedor = @codigo;
                            INSERT INTO producto_codigos_proveedor (proveedor_id, codigo_proveedor, producto_id, unidades_por)
                            VALUES (@proveedorId, @codigo, @productoId, @unidadesPor)",
                            new { negocio, proveedorId, codigo = linea.CodigoProveedor, productoId, unidadesPor }, tx);
                    }
                    sumados++;
                }

                await con.ExecuteAsync(@"
                    INSERT INTO compra_items (compra_id, producto_id, codigo_proveedor, descripcion, cantidad, unidades_por, valor_total)
                    VALUES (@compraId, @productoId, @codigo, @descripcion, @cantidad, @unidadesPor, @valor)",
                    new
                    {
                        compraId, productoId, codigo = linea.CodigoProveedor,
                        descripcion = Recortar(linea.Descripcion, 300), cantidad = linea.Cantidad, unidadesPor, valor = linea.ValorTotal
                    }, tx);
            }

            await tx.CommitAsync();
            TempData["Mensaje"] = $"Factura {Factura.Numero} de {Factura.ProveedorNombre} cargada: {sumados} productos sumados al inventario"
                + (creados > 0 ? $" ({creados} nuevos)" : "") + ".";
            return RedirectToPage("/Compras/Index");
        }
        catch (InvalidOperationException ex)
        {
            await tx.RollbackAsync();
            Error = ex.Message;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await tx.RollbackAsync();
            Error = ex.ConstraintName == "ux_productos_codigo_barras"
                ? "Uno de los productos nuevos tiene un código de barras que ya usa otro producto. Elige ese producto en la lista."
                : ex.ConstraintName == "productos_negocio_codigo_key"
                    ? "Uno de los productos nuevos tiene un código que ya existe. Cambia el código o elige el producto existente."
                    : "Esta factura ya estaba cargada.";
        }

        await CargarProductosAsync(con, negocio);
        return Page();
    }

    // ---------- Ayudas ----------

    private FacturaElectronica.Factura LeerGuardado(string archivo)
    {
        using var s = System.IO.File.OpenRead(Path.Combine(Carpeta, archivo));
        return FacturaElectronica.Leer(s, archivo);
    }

    private static string Nit(FacturaElectronica.Factura f) => f.ProveedorNit.Length > 0 ? f.ProveedorNit : "SIN-NIT " + f.ProveedorNombre;

    private static async Task<string?> YaCargadaAsync(NpgsqlConnection con, FacturaElectronica.Factura f, NpgsqlTransaction? tx = null)
    {
        var existente = await con.QueryFirstOrDefaultAsync<(DateTime Creado, string Negocio)?>(@"
            SELECT c.creado_en, c.negocio FROM compras c JOIN proveedores p ON p.id = c.proveedor_id
            WHERE (c.cufe <> '' AND c.cufe = @Cufe) OR (p.nit = @nit AND c.numero_factura = @Numero)
            LIMIT 1", new { f.Cufe, nit = Nit(f), f.Numero }, tx);

        return existente is { } e
            ? $"La factura {f.Numero} ya se cargó el {e.Creado:dd/MM/yyyy} en {Negocio.Nombre(e.Negocio)}. No se puede cargar dos veces."
            : null;
    }

    private async Task CargarProductosAsync(NpgsqlConnection con, string negocio)
    {
        Productos = (await con.QueryAsync<ProductoOpcion>(@"
            SELECT id AS Id, codigo AS Codigo, nombre AS Nombre, medida AS Medida, codigo_barras AS CodigoBarras
            FROM productos WHERE negocio = @negocio ORDER BY nombre, medida", new { negocio })).ToList();
    }

    // Por cada línea: el producto que ya se usó antes con ese código del proveedor,
    // o el que tiene el mismo código de barras. Si no hay, se propone crearlo.
    private async Task<List<LineaForm>> SugerenciasAsync(NpgsqlConnection con, string negocio, FacturaElectronica.Factura f)
    {
        var resultado = new List<LineaForm>();
        foreach (var l in f.Lineas)
        {
            var aprendido = await con.QueryFirstOrDefaultAsync<(int ProductoId, int UnidadesPor)?>(@"
                SELECT pc.producto_id, pc.unidades_por
                FROM producto_codigos_proveedor pc
                JOIN proveedores pv ON pv.id = pc.proveedor_id
                JOIN productos p ON p.id = pc.producto_id
                WHERE pv.nit = @nit AND pc.codigo_proveedor = @codigo AND p.negocio = @negocio
                LIMIT 1", new { nit = Nit(f), codigo = l.CodigoProveedor, negocio });

            int? porBarras = l.CodigoBarras is null ? null : Productos.FirstOrDefault(p => p.CodigoBarras == l.CodigoBarras)?.Id;

            resultado.Add(new LineaForm
            {
                ProductoId = aprendido?.ProductoId ?? porBarras ?? CrearNuevo,
                UnidadesPor = aprendido?.UnidadesPor ?? 1,
                CodigoNuevo = Recortar(l.CodigoProveedor, 30),
                NombreNuevo = Recortar(l.Descripcion, 150)
            });
        }
        return resultado;
    }

    private static async Task<int> CrearProductoAsync(NpgsqlConnection con, NpgsqlTransaction tx, string negocio,
        FacturaElectronica.Linea linea, LineaForm form, decimal costoUnidad)
    {
        var codigo = (form.CodigoNuevo ?? "").Trim();
        var nombre = (form.NombreNuevo ?? "").Trim();
        if (codigo.Length == 0) codigo = "P" + DateTime.Now.ToString("yyMMddHHmmss") + linea.Numero;
        if (nombre.Length == 0) nombre = linea.Descripcion;
        if (codigo.Length > 30 || nombre.Length > 150)
            throw new InvalidOperationException($"Línea {linea.Numero}: el código (máx. 30) o el nombre (máx. 150) es muy largo.");

        var barras = linea.CodigoBarras ?? await CodigoBarras.GenerarAsync(con, tx);
        return await con.ExecuteScalarAsync<int>(@"
            INSERT INTO productos (negocio, codigo, codigo_barras, nombre, precio_compra, stock, stock_minimo)
            VALUES (@negocio, @codigo, @barras, @nombre, @costoUnidad, 0, 0)
            RETURNING id", new { negocio, codigo, barras, nombre = Recortar(nombre, 150), costoUnidad }, tx);
    }

    private static string Recortar(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo];
}
