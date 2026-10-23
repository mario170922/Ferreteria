using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Compras;

// Cargar una factura de compra en dos pasos:
// 1. "Leer": se sube el .zip (o .xml) del correo del proveedor. Se revisa que la factura cuadre
//    y cada línea se compara con el inventario (Services/Homologacion.cs): sale el producto
//    sugerido con su % de confianza, la explicación y un estado (automático, requiere
//    aprobación o sin coincidencia). Leer la factura NO toca el inventario.
// 2. "Guardar": la persona revisa, corrige lo que haga falta (producto, cantidad, unidades por
//    caja) y confirma. Todo va en una sola transacción: se suma al inventario, se actualiza el
//    precio de compra, se guardan los alias confirmados y las correcciones, y la compra queda
//    como egreso. Si algo falla, no queda nada a medias.
// El archivo se guarda en App_Data/facturas para poder consultarlo después.
[Authorize(Roles = "admin")]
[RequestSizeLimit(20_000_000)]
public class CargarModel : PageModel
{
    public const int CrearNuevo = 0;
    public const int NoSumar = -1;
    public const int Elegir = -2;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _entorno;

    public CargarModel(IConfiguration config, IWebHostEnvironment entorno)
    {
        _config = config;
        _entorno = entorno;
    }

    public FacturaElectronica.Factura? Factura { get; set; }
    public List<ProductoOpcion> Productos { get; set; } = new();
    public List<Homologacion.Resultado> Resultados { get; set; } = new();
    public List<string> Alertas { get; set; } = new();
    public string? Error { get; set; }

    [BindProperty] public string? Archivo { get; set; }          // nombre del archivo guardado en el paso 1
    [BindProperty] public List<LineaForm> Lineas { get; set; } = new();

    public class LineaForm
    {
        public int ProductoId { get; set; } = Elegir;
        public int UnidadesPor { get; set; } = 1;
        public decimal? Cantidad { get; set; }         // corregible si la lectura quedó mal
        public bool Aprobado { get; set; }
        public bool GuardarAlias { get; set; } = true;
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
        public int Stock { get; set; }
        public string Texto => $"{Nombre}{(string.IsNullOrWhiteSpace(Medida) ? "" : " · " + Medida)} ({Codigo})";
    }

    private string Carpeta => Path.Combine(_entorno.ContentRootPath, "App_Data", "facturas");

    private Homologacion.Umbrales Umbrales => new()
    {
        Automatico = int.TryParse(_config["Homologacion:Automatico"], out var a) ? a : 90,
        Sugerir = int.TryParse(_config["Homologacion:Sugerir"], out var s) ? s : 60
    };

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
        catch (Exception ex)
        {
            System.IO.File.Delete(Path.Combine(Carpeta, Archivo));
            Error = ex is InvalidOperationException ? ex.Message : "El archivo no es una factura electrónica válida.";
            return Page();
        }

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var negocio = User.NegocioActual();

        if (await YaCargadaAsync(con, Factura) is { } aviso)
        {
            System.IO.File.Delete(Path.Combine(Carpeta, Archivo));
            Error = aviso;
            Factura = null;
            return Page();
        }

        await CargarProductosAsync(con, negocio);
        Alertas = FacturaElectronica.Validar(Factura);
        Resultados = await HomologarAsync(con, negocio, Factura);
        Lineas = ValoresIniciales(Factura, Resultados);
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
        await CargarProductosAsync(con, negocio);
        Alertas = FacturaElectronica.Validar(Factura);
        Resultados = await HomologarAsync(con, negocio, Factura);

        if (Lineas.Count != Factura.Lineas.Count)
        {
            Error = "Los productos no coinciden con la factura. Vuelve a cargarla.";
            return Page();
        }

        // Nada entra al inventario sin una decisión segura
        var faltan = new List<string>();
        for (var i = 0; i < Lineas.Count; i++)
        {
            var (form, res, n) = (Lineas[i], Resultados[i], Factura.Lineas[i].Numero);
            if (form.ProductoId == Elegir)
                faltan.Add($"Línea {n}: elige el producto, créalo nuevo o marca \"No sumar\".");
            else if (res.Estado == Homologacion.Aprobar && form.ProductoId == res.ProductoId && !form.Aprobado)
                faltan.Add($"Línea {n}: marca \"Aprobado\" para confirmar la sugerencia ({res.Confianza} % de confianza).");
            if (form.Cantidad is <= 0)
                faltan.Add($"Línea {n}: la cantidad debe ser mayor que cero.");
        }
        if (faltan.Count > 0)
        {
            Error = string.Join(" ", faltan);
            return Page();
        }

        var usuarioId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var u) ? u : (int?)null;

        await using var tx = await con.BeginTransactionAsync();
        try
        {
            // Si dos personas guardan la misma factura al tiempo, la segunda espera aquí
            // y luego la detecta como repetida.
            await con.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext(@llave))",
                new { llave = Nit(Factura) + "|" + Factura.Numero }, tx);

            if (await YaCargadaAsync(con, Factura, tx) is { } aviso)
                throw new InvalidOperationException(aviso);

            var proveedorId = await con.ExecuteScalarAsync<int>(@"
                INSERT INTO proveedores (nit, nombre) VALUES (@nit, @nombre)
                ON CONFLICT (nit) DO UPDATE SET nombre = EXCLUDED.nombre
                RETURNING id", new { nit = Nit(Factura), nombre = Recortar(Factura.ProveedorNombre, 200) }, tx);

            var compraId = await con.ExecuteScalarAsync<int>(@"
                INSERT INTO compras (negocio, proveedor_id, numero_factura, cufe, fecha, subtotal, impuestos, total,
                                     usuario_id, moneda, descuentos, cargos, archivo, alertas)
                VALUES (@negocio, @proveedorId, @Numero, @Cufe, @Fecha, @Subtotal, @Impuestos, @Total,
                        @usuarioId, @Moneda, @Descuentos, @Cargos, @Archivo, @alertas)
                RETURNING id",
                new
                {
                    negocio, proveedorId, Factura.Numero, Factura.Cufe, Factura.Fecha, Factura.Subtotal, Factura.Impuestos, Factura.Total,
                    usuarioId, Moneda = Recortar(Factura.Moneda, 3), Factura.Descuentos, Factura.Cargos, Archivo,
                    alertas = Alertas.Count == 0 ? null : Recortar(string.Join(" ", Alertas), 1000)
                }, tx);

            var sumados = 0;
            var creados = 0;
            // Un mismo producto puede venir en varias líneas (por ejemplo, una de obsequio con valor 0):
            // se crea una sola vez y su costo se calcula con todas sus líneas juntas.
            var creadosEnEstaFactura = new Dictionary<string, int>();
            var acumulado = new Dictionary<int, (int Unidades, decimal Valor)>();
            for (var i = 0; i < Factura.Lineas.Count; i++)
            {
                var linea = Factura.Lineas[i];
                var form = Lineas[i];
                var res = Resultados[i];
                var unidadesPor = Math.Max(1, form.UnidadesPor);
                var cantidadFactura = form.Cantidad is > 0 ? form.Cantidad.Value : linea.Cantidad;
                int? productoId = null;

                if (form.ProductoId != NoSumar)
                {
                    var unidades = cantidadFactura * unidadesPor;
                    if (unidades != Math.Floor(unidades))
                        throw new InvalidOperationException($"Línea {linea.Numero}: la cantidad ({unidades:0.###}) no da unidades enteras. Revisa \"unidades por cada una\".");
                    var cantidad = (int)unidades;
                    // Costo de cada unidad con impuestos incluidos: lo que realmente se pagó
                    var costoUnidad = cantidad > 0 ? Math.Round(linea.ValorTotal / cantidad, 2) : 0;

                    // Solo se reutiliza si es el mismo artículo del proveedor (mismo código de barras o del proveedor)
                    var llaveNuevo = linea.CodigoBarras ?? linea.CodigoProveedor;
                    if (form.ProductoId == CrearNuevo && llaveNuevo.Length > 0 && creadosEnEstaFactura.TryGetValue(llaveNuevo, out var yaCreado))
                    {
                        productoId = yaCreado;
                    }
                    else if (form.ProductoId == CrearNuevo)
                    {
                        productoId = await CrearProductoAsync(con, tx, negocio, linea, form, costoUnidad);
                        if (llaveNuevo.Length > 0) creadosEnEstaFactura[llaveNuevo] = productoId.Value;
                        creados++;
                    }
                    else
                    {
                        productoId = await con.ExecuteScalarAsync<int?>(
                            "SELECT id FROM productos WHERE id = @id AND negocio = @negocio FOR UPDATE",
                            new { id = form.ProductoId, negocio }, tx)
                            ?? throw new InvalidOperationException($"Línea {linea.Numero}: el producto elegido no existe en este negocio.");
                    }

                    await con.ExecuteAsync(
                        "UPDATE productos SET stock = stock + @cantidad WHERE id = @productoId",
                        new { cantidad, productoId }, tx);

                    var previo = acumulado.GetValueOrDefault(productoId.Value);
                    acumulado[productoId.Value] = (previo.Unidades + cantidad, previo.Valor + linea.ValorTotal);

                    await con.ExecuteAsync(@"
                        INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
                        VALUES (@productoId, @cantidad, @motivo)",
                        new { productoId, cantidad, motivo = Recortar($"Compra: factura {Factura.Numero} de {Factura.ProveedorNombre}", 200) }, tx);

                    // Lo que la persona confirmó se guarda como alias para las próximas facturas
                    if (form.GuardarAlias)
                        await GuardarAliasAsync(con, tx, proveedorId, productoId.Value, linea, unidadesPor, usuarioId);
                    sumados++;
                }

                // Correcciones frente a lo que sugirió el sistema (para mejorar la homologación)
                var detalles = new List<string>();
                if (res.ProductoId is not null && form.ProductoId != res.ProductoId)
                    detalles.Add(form.ProductoId switch { CrearNuevo => "creó un producto nuevo", NoSumar => "no lo sumó al inventario", _ => "eligió otro producto" });
                if (cantidadFactura != linea.Cantidad) detalles.Add($"cantidad corregida de {linea.Cantidad:0.###} a {cantidadFactura:0.###}");
                if (unidadesPor != res.Factor) detalles.Add($"unidades por cada una: {res.Factor} → {unidadesPor}");
                if (detalles.Count > 0)
                {
                    await con.ExecuteAsync(@"
                        INSERT INTO homologacion_correcciones (compra_id, proveedor_id, codigo_proveedor, descripcion,
                            producto_sugerido, confianza, producto_elegido, detalle, usuario_id)
                        VALUES (@compraId, @proveedorId, @codigo, @descripcion, @sugerido, @confianza, @elegido, @detalle, @usuarioId)",
                        new
                        {
                            compraId, proveedorId, codigo = Nulo(Recortar(linea.CodigoProveedor, 60)), descripcion = Recortar(linea.Descripcion, 300),
                            sugerido = res.ProductoId, confianza = res.Confianza, elegido = productoId,
                            detalle = Recortar(string.Join("; ", detalles), 300), usuarioId
                        }, tx);
                }

                await con.ExecuteAsync(@"
                    INSERT INTO compra_items (compra_id, producto_id, codigo_proveedor, descripcion, cantidad, unidades_por, valor_total,
                                              unidad, cantidad_factura, valor_sin_impuestos, impuestos, descuento, estado_coincidencia, confianza)
                    VALUES (@compraId, @productoId, @codigo, @descripcion, @cantidad, @unidadesPor, @valor,
                            @unidad, @cantidadLeida, @sinImpuestos, @impuestos, @descuento, @estado, @confianza)",
                    new
                    {
                        compraId, productoId, codigo = Recortar(linea.CodigoProveedor, 60),
                        descripcion = Recortar(linea.Descripcion, 300), cantidad = cantidadFactura, unidadesPor, valor = linea.ValorTotal,
                        unidad = Nulo(Recortar(linea.Unidad ?? "", 20)), cantidadLeida = linea.Cantidad,
                        sinImpuestos = linea.ValorSinImpuestos, impuestos = linea.Impuestos, descuento = linea.Descuento,
                        estado = res.Estado, confianza = res.Confianza
                    }, tx);
            }

            // Precio de compra = lo pagado por ese producto en toda la factura / unidades recibidas
            foreach (var (id, (unidadesTotales, valor)) in acumulado)
            {
                if (unidadesTotales <= 0) continue;
                // El costo calculado es por unidad; si el producto maneja precio por ciento, se multiplica
                await con.ExecuteAsync("UPDATE productos SET precio_compra = @costo * precio_por WHERE id = @id",
                    new { costo = Math.Round(valor / unidadesTotales, 2), id }, tx);
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
        catch (PostgresException)
        {
            await tx.RollbackAsync();
            Error = "No se pudo guardar la compra. Revisa que los códigos y nombres no sean demasiado largos e intenta de nuevo.";
        }

        return Page();
    }

    // ---------- Ayudas ----------

    private FacturaElectronica.Factura LeerGuardado(string archivo)
    {
        using var s = System.IO.File.OpenRead(Path.Combine(Carpeta, archivo));
        return FacturaElectronica.Leer(s, archivo);
    }

    // Si la factura no trae NIT, se usa una clave corta sacada del nombre (la columna admite 20 letras)
    private static string Nit(FacturaElectronica.Factura f) => f.ProveedorNit.Length > 0
        ? (f.ProveedorNit.Length > 20 ? f.ProveedorNit[..20] : f.ProveedorNit)
        : "SN-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
              System.Text.Encoding.UTF8.GetBytes(f.ProveedorNombre.ToUpperInvariant())))[..12];

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
            SELECT id AS Id, codigo AS Codigo, nombre AS Nombre, medida AS Medida, codigo_barras AS CodigoBarras, stock AS Stock
            FROM productos WHERE negocio = @negocio ORDER BY nombre, medida", new { negocio })).ToList();
    }

    // Compara cada línea con el inventario usando los alias confirmados de este proveedor
    private async Task<List<Homologacion.Resultado>> HomologarAsync(NpgsqlConnection con, string negocio, FacturaElectronica.Factura f)
    {
        var alias = (await con.QueryAsync<(int ProductoId, string? Codigo, string Normalizada, int Factor)>(@"
            SELECT a.producto_id, a.codigo_proveedor, a.descripcion_normalizada, a.factor
            FROM producto_alias a
            JOIN proveedores pv ON pv.id = a.proveedor_id
            JOIN productos p ON p.id = a.producto_id
            WHERE pv.nit = @nit AND p.negocio = @negocio
            ORDER BY a.ultima_confirmacion DESC", new { nit = Nit(f), negocio }))
            .Select(a => new Homologacion.Alias(a.ProductoId, a.Codigo, a.Normalizada, a.Factor)).ToList();

        var productos = Productos.Select(p => new Homologacion.Producto(p.Id, p.Nombre, p.Medida, p.CodigoBarras)).ToList();
        var umbrales = Umbrales;
        return f.Lineas.Select(l => Homologacion.Identificar(
            l.CodigoProveedor, l.CodigoBarras, l.Descripcion, alias, productos, umbrales)).ToList();
    }

    // Lo que sale marcado al abrir la revisión
    private static List<LineaForm> ValoresIniciales(FacturaElectronica.Factura f, List<Homologacion.Resultado> res)
    {
        var lista = new List<LineaForm>();
        for (var i = 0; i < f.Lineas.Count; i++)
        {
            var l = f.Lineas[i];
            var r = res[i];

            // Si el producto viene repetido en la factura (por ejemplo una línea de obsequio),
            // la segunda línea toma lo mismo que la primera
            var anterior = l.CodigoProveedor.Length > 0
                ? Enumerable.Range(0, i).Where(j => f.Lineas[j].CodigoProveedor == l.CodigoProveedor).Select(j => lista[j]).FirstOrDefault()
                : null;

            lista.Add(new LineaForm
            {
                ProductoId = anterior?.ProductoId ?? (r.Estado == Homologacion.SinCoincidencia ? Elegir : r.ProductoId ?? Elegir),
                UnidadesPor = anterior?.UnidadesPor ?? r.Factor,
                Cantidad = l.Cantidad,
                Aprobado = anterior?.Aprobado ?? false,
                GuardarAlias = true,
                CodigoNuevo = Recortar(l.CodigoProveedor, 30),
                NombreNuevo = Recortar(l.Descripcion, 150)
            });
        }
        return lista;
    }

    private static async Task GuardarAliasAsync(NpgsqlConnection con, NpgsqlTransaction tx, int proveedorId, int productoId,
        FacturaElectronica.Linea linea, int factor, int? usuarioId)
    {
        var datos = new
        {
            productoId, proveedorId, codigo = Nulo(Recortar(linea.CodigoProveedor, 60)),
            original = Recortar(linea.Descripcion, 300), normal = Recortar(Homologacion.Normalizar(linea.Descripcion), 300),
            unidad = Nulo(Recortar(linea.Unidad ?? "", 20)), factor, usuarioId
        };

        // Se actualiza el alias si ya existía para este proveedor, código (o descripción) y producto
        var filas = await con.ExecuteAsync(@"
            UPDATE producto_alias SET descripcion_original = @original, descripcion_normalizada = @normal,
                unidad_proveedor = @unidad, factor = @factor, veces = veces + 1,
                ultima_confirmacion = now(), confirmado_por = @usuarioId
            WHERE proveedor_id = @proveedorId AND producto_id = @productoId
              AND (codigo_proveedor = @codigo OR (@codigo IS NULL AND codigo_proveedor IS NULL AND descripcion_normalizada = @normal))",
            datos, tx);

        if (filas == 0)
        {
            await con.ExecuteAsync(@"
                INSERT INTO producto_alias (producto_id, proveedor_id, codigo_proveedor, descripcion_original,
                    descripcion_normalizada, unidad_proveedor, factor, confirmado_por)
                VALUES (@productoId, @proveedorId, @codigo, @original, @normal, @unidad, @factor, @usuarioId)",
                datos, tx);
        }
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

    private static string? Nulo(string texto) => string.IsNullOrWhiteSpace(texto) ? null : texto;
}
