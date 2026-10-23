using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Pedidos;

// Hacen pedidos el administrador y los vendedores.
// Venta de mostrador (?mostrador=true): el cliente compra en la tienda y paga ahí mismo;
// la hacen el administrador y el cajero (el cajero solo hace ventas de mostrador).
[Authorize(Roles = "admin,vendedor,cajero")]
public class NuevoPedidoModel : PageModel
{
    private const int MaxLineas = 10;
    private const int MaxCantidadPorLinea = 100000;

    private readonly IConfiguration _config;

    public NuevoPedidoModel(IConfiguration config)
    {
        _config = config;
    }

    public List<ProductoOpcion> Productos { get; set; } = new();
    public decimal DescuentoMaximo { get; set; }
    public string NombreVendedor { get; set; } = "";

    [BindProperty]
    public decimal DescuentoElegido { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Mostrador { get; set; }

    // Venta de mostrador: cómo pagó y comprobante de la transferencia
    [BindProperty]
    public string? PagoMetodo { get; set; }

    [BindProperty]
    public string? PagoReferencia { get; set; }

    public bool PuedeMostrador => User.IsInRole(Negocio.Admin) || User.IsInRole(Negocio.Cajero);

    private const string CelularMostrador = "MOSTRADOR";

    [BindProperty]
    public DatosCliente Cliente { get; set; } = new();

    [BindProperty]
    public List<LineaPedido> Lineas { get; set; } = new();

    // ---------- Carga inicial de la pantalla ----------

    public async Task OnGetAsync()
    {
        AjustarModo();
        Lineas = Enumerable.Range(0, MaxLineas).Select(_ => new LineaPedido()).ToList();

        await using var con = CrearConexion();
        await con.OpenAsync();
        await CargarDatosAsync(con);
    }

    // ---------- Búsqueda de cliente por celular (GET ?handler=Cliente&cel=...) ----------

    public async Task<IActionResult> OnGetClienteAsync(string cel)
    {
        var buscado = (cel ?? "").Trim();
        if (buscado.Length == 0)
        {
            return new JsonResult(new { encontrado = false });
        }

        await using var con = CrearConexion();
        await con.OpenAsync();

        var c = await con.QueryFirstOrDefaultAsync<ClienteEncontrado>(@"
            SELECT id                AS Id,
                   nombre_ferreteria AS NombreFerreteria,
                   nombre_cliente    AS NombreCliente,
                   direccion         AS Direccion,
                   ubicacion         AS Ubicacion,
                   horario           AS Horario,
                   celular_2         AS Celular2
            FROM clientes
            WHERE celular_1 = @c OR celular_2 = @c
            ORDER BY id
            LIMIT 1", new { c = buscado });

        if (c is null)
        {
            return new JsonResult(new { encontrado = false });
        }

        // Historial del cliente con cualquier vendedor.
        // Los últimos pedidos se muestran todos, con su estado.
        var ultimos = await con.QueryAsync<PedidoAnterior>(@"
            SELECT numero AS Numero, fecha AS Fecha, estado AS Estado, total AS Total
            FROM pedidos
            WHERE cliente_id = @id AND negocio = @negocio
            ORDER BY fecha DESC, id DESC
            LIMIT 5", new { id = c.Id, negocio = User.NegocioActual() });

        // Para contar compras, los pedidos cancelados o no entregados no cuentan.
        var resumen = await con.QueryFirstAsync<ResumenCompras>(@"
            SELECT count(*) AS Pedidos, max(fecha) AS Ultima
            FROM pedidos
            WHERE cliente_id = @id AND negocio = @negocio AND estado NOT IN ('cancelado', 'no_entregado')",
            new { id = c.Id, negocio = User.NegocioActual() });

        // Lo que más compra: en cuántos pedidos lo llevó, cuándo fue la última vez
        // y cuántas unidades llevó esa vez, para repetirla con un toque.
        var frecuentes = await con.QueryAsync<ProductoFrecuente>(@"
            SELECT pr.id      AS Id,
                   pr.codigo  AS Codigo,
                   pr.nombre  AS Nombre,
                   pr.medida  AS Medida,
                   pr.stock   AS Stock,
                   count(DISTINCT p.id) AS Veces,
                   max(p.fecha)         AS UltimaFecha,
                   (array_agg(pi.cantidad ORDER BY p.fecha DESC, pi.id DESC))[1] AS UltimaCantidad
            FROM pedido_items pi
            JOIN pedidos p    ON p.id = pi.pedido_id
            JOIN productos pr ON pr.id = pi.producto_id
            WHERE p.cliente_id = @id AND p.negocio = @negocio AND p.estado NOT IN ('cancelado', 'no_entregado')
            GROUP BY pr.id
            ORDER BY count(DISTINCT p.id) DESC, max(p.fecha) DESC
            LIMIT 8", new { id = c.Id, negocio = User.NegocioActual() });

        return new JsonResult(new
        {
            encontrado = true,
            nombreFerreteria = c.NombreFerreteria ?? "",
            nombreCliente = c.NombreCliente,
            direccion = c.Direccion,
            ubicacion = c.Ubicacion ?? "",
            horario = c.Horario ?? "",
            celular2 = c.Celular2 ?? "",
            pedidos = resumen.Pedidos,
            ultimaCompra = resumen.Ultima?.ToString("dd/MM/yyyy"),
            ultimos = ultimos.Select(u => new
            {
                numero = u.Numero,
                fecha = u.Fecha.ToString("dd/MM/yyyy"),
                estado = ListaModel.EtiquetaEstado(u.Estado),
                claseEstado = ListaModel.ClaseEstado(u.Estado),
                total = u.Total
            }),
            frecuentes = frecuentes.Select(f => new
            {
                id = f.Id,
                codigo = f.Codigo,
                nombre = f.Nombre,
                medida = f.Medida ?? "",
                stock = f.Stock,
                veces = f.Veces,
                ultimaFecha = f.UltimaFecha.ToString("dd/MM/yyyy"),
                ultimaCantidad = f.UltimaCantidad
            })
        });
    }

    public class ResumenCompras
    {
        public long Pedidos { get; set; }
        public DateTime? Ultima { get; set; }
    }

    public class PedidoAnterior
    {
        public string Numero { get; set; } = "";
        public DateTime Fecha { get; set; }
        public string Estado { get; set; } = "";
        public decimal Total { get; set; }
    }

    public class ProductoFrecuente
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int Stock { get; set; }
        public long Veces { get; set; }
        public DateTime UltimaFecha { get; set; }
        public int UltimaCantidad { get; set; }
    }

    // ---------- Guardar pedido ----------

    public async Task<IActionResult> OnPostAsync()
    {
        AjustarModo();
        await using var con = CrearConexion();
        await con.OpenAsync();

        ValidarCliente();
        var lineas = ValidarLineas();
        if (Mostrador && PagoMetodo is not ("efectivo" or "transferencia"))
            ModelState.AddModelError(string.Empty, "Elige cómo pagó el cliente: efectivo o transferencia.");

        if (!ModelState.IsValid)
        {
            await CargarDatosAsync(con);
            return Page();
        }

        await using var tx = await con.BeginTransactionAsync();

        try
        {
            // El máximo sale de la tabla usuarios, nunca del formulario.
            var vendedor = await con.QueryFirstOrDefaultAsync<VendedorInfo>(@"
                SELECT id AS Id, descuento_pct AS DescuentoPct
                FROM usuarios
                WHERE lower(nombre_usuario) = lower(@n) AND activo = true",
                new { n = User.Identity?.Name ?? "" }, tx);

            if (vendedor is null)
            {
                throw new InvalidOperationException("Tu usuario no está activo. Consulta con el administrador.");
            }

            // El administrador puede dar cualquier descuento; el vendedor, hasta su máximo.
            decimal maximo = User.IsInRole("admin") ? 100m : vendedor.DescuentoPct;

            if (DescuentoElegido < 0 || DescuentoElegido > maximo)
            {
                throw new InvalidOperationException(
                    $"El descuento elegido supera tu máximo permitido ({maximo:0.##}%).");
            }

            int idCliente = await ObtenerOCrearClienteAsync(con, tx);

            decimal subtotal = 0m;
            var detalles = new List<(int ProductoId, int Cantidad, decimal Precio)>();

            foreach (var (productoId, cantidad) in lineas)
            {
                var prod = await con.QueryFirstOrDefaultAsync<ProductoStock>(@"
                    SELECT nombre        AS Nombre,
                           round(precio_venta / precio_por, 2) AS PrecioVenta,
                           stock         AS Stock
                    FROM productos
                    WHERE id = @id AND negocio = @negocio
                    FOR UPDATE", new { id = productoId, negocio = User.NegocioActual() }, tx);

                if (prod is null)
                {
                    throw new InvalidOperationException("Uno de los productos ya no existe.");
                }

                if (prod.Stock < cantidad)
                {
                    throw new InvalidOperationException(
                        $"No hay suficiente stock de {prod.Nombre}. Disponible: {prod.Stock:N0}.");
                }

                await con.ExecuteAsync(
                    "UPDATE productos SET stock = stock - @cant WHERE id = @id",
                    new { cant = cantidad, id = productoId }, tx);

                subtotal += prod.PrecioVenta * cantidad;
                detalles.Add((productoId, cantidad, prod.PrecioVenta));
            }

            // Pesos colombianos: sin centavos.
            decimal total = Math.Round(
                subtotal * (1 - DescuentoElegido / 100m),
                0,
                MidpointRounding.AwayFromZero);

            int pedidoId = await con.ExecuteScalarAsync<int>(@"
                INSERT INTO pedidos (negocio, cliente_id, vendedor_id, descuento_pct, total)
                VALUES (@negocio, @cliente, @vendedor, @pct, @total)
                RETURNING id",
                new
                {
                    negocio = User.NegocioActual(),
                    cliente = idCliente,
                    vendedor = vendedor.Id,
                    pct = DescuentoElegido,
                    total
                }, tx);

            foreach (var d in detalles)
            {
                await con.ExecuteAsync(@"
                    INSERT INTO pedido_items (pedido_id, producto_id, cantidad, precio_unitario)
                    VALUES (@pedido, @producto, @cantidad, @precio)",
                    new
                    {
                        pedido = pedidoId,
                        producto = d.ProductoId,
                        cantidad = d.Cantidad,
                        precio = d.Precio
                    }, tx);
            }

            // Cada producto del pedido queda en el historial de inventario como salida.
            await con.ExecuteAsync(@"
                INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
                SELECT pi.producto_id, -pi.cantidad, 'Salida por pedido ' || p.numero
                FROM pedido_items pi
                JOIN pedidos p ON p.id = pi.pedido_id
                WHERE pi.pedido_id = @id", new { id = pedidoId }, tx);

            // Venta de mostrador: queda entregada y cobrada en el momento
            if (Mostrador)
            {
                await con.ExecuteAsync(@"
                    UPDATE pedidos SET tipo = 'mostrador', estado = 'entregado', entregado_en = now(),
                        pago_metodo = @metodo, pago_valor = total, pago_referencia = @referencia
                    WHERE id = @id",
                    new { id = pedidoId, metodo = PagoMetodo, referencia = Nulo(PagoReferencia) }, tx);
            }

            await tx.CommitAsync();
            if (Mostrador) TempData["Mensaje"] = "Venta de mostrador registrada y cobrada.";
            return RedirectToPage("/Pedidos/Detalle", new { id = pedidoId });
        }
        catch (InvalidOperationException ex)
        {
            await tx.RollbackAsync();
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception)
        {
            await tx.RollbackAsync();
            ModelState.AddModelError(string.Empty, "No se pudo guardar el pedido. Intenta de nuevo.");
        }

        await CargarDatosAsync(con);
        return Page();
    }

    // ---------- Ayudas ----------

    private NpgsqlConnection CrearConexion()
    {
        return new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
    }

    private async Task CargarDatosAsync(NpgsqlConnection con)
    {
        NombreVendedor = User.Identity?.Name ?? "";

        DescuentoMaximo = await con.QueryFirstOrDefaultAsync<decimal?>(
            "SELECT descuento_pct FROM usuarios WHERE lower(nombre_usuario) = lower(@n)",
            new { n = NombreVendedor }) ?? 0m;

        if (User.IsInRole("admin"))
        {
            DescuentoMaximo = 100m;
        }

        var productos = await con.QueryAsync<ProductoOpcion>(@"
            SELECT id           AS Id,
                   codigo       AS Codigo,
                   codigo_barras AS CodigoBarras,
                   nombre       AS Nombre,
                   medida       AS Medida,
                   round(precio_venta / precio_por, 2) AS PrecioVenta,
                   precio_por   AS PrecioPor,
                   stock        AS Stock,
                   imagen       AS Imagen
            FROM productos
            WHERE stock > 0 AND negocio = @negocio
            ORDER BY codigo", new { negocio = User.NegocioActual() });

        Productos = productos.ToList();
    }

    // El cajero solo vende en mostrador; el vendedor nunca
    private void AjustarModo()
    {
        if (User.IsInRole(Negocio.Cajero) && !User.IsInRole(Negocio.Admin)) Mostrador = true;
        if (!PuedeMostrador) Mostrador = false;
    }

    private void ValidarCliente()
    {
        // En mostrador el cliente es opcional: si no se escribe celular, queda como "Cliente de mostrador"
        if (Mostrador)
        {
            if (!string.IsNullOrWhiteSpace(Cliente.Celular1) && string.IsNullOrWhiteSpace(Cliente.NombreCliente))
                ModelState.AddModelError(string.Empty, "Escribe el nombre del cliente, o borra el celular para venderle a un cliente de mostrador.");
            RevisarLargo(Cliente.Celular1, 20, "El celular principal");
            RevisarLargo(Cliente.NombreCliente, 150, "El nombre del cliente");
            return;
        }

        if (string.IsNullOrWhiteSpace(Cliente.Celular1))
        {
            ModelState.AddModelError(string.Empty, "Escribe el celular del cliente.");
        }

        if (string.IsNullOrWhiteSpace(Cliente.NombreCliente))
        {
            ModelState.AddModelError(string.Empty, "Escribe el nombre del cliente.");
        }

        if (string.IsNullOrWhiteSpace(Cliente.Direccion))
        {
            ModelState.AddModelError(string.Empty, "Escribe la dirección del cliente.");
        }

        // Los mismos límites que tiene la tabla clientes, con un mensaje claro.
        RevisarLargo(Cliente.Celular1, 20, "El celular principal");
        RevisarLargo(Cliente.Celular2, 20, "El otro celular");
        RevisarLargo(Cliente.NombreFerreteria, 150, "El nombre de la ferretería");
        RevisarLargo(Cliente.NombreCliente, 150, "El nombre del cliente");
        RevisarLargo(Cliente.Direccion, 250, "La dirección");
        RevisarLargo(Cliente.Ubicacion, 250, "La ubicación");
        RevisarLargo(Cliente.Horario, 150, "El horario");
    }

    private void RevisarLargo(string? texto, int maximo, string campo)
    {
        if ((texto ?? "").Trim().Length > maximo)
        {
            ModelState.AddModelError(string.Empty, $"{campo} es muy largo (máximo {maximo} letras).");
        }
    }

    private List<(int ProductoId, int Cantidad)> ValidarLineas()
    {
        var validas = new List<(int ProductoId, int Cantidad)>();

        for (int i = 0; i < Lineas.Count; i++)
        {
            var l = Lineas[i];
            int cantidad = l.Cantidad ?? 0;

            // Fila vacía: se ignora
            if (l.ProductoId == 0 && cantidad == 0)
            {
                continue;
            }

            if (l.ProductoId == 0)
            {
                ModelState.AddModelError(string.Empty, $"La línea {i + 1} tiene cantidad, pero no tiene producto.");
                continue;
            }

            if (cantidad < 1 || cantidad > MaxCantidadPorLinea)
            {
                ModelState.AddModelError(string.Empty,
                    $"La línea {i + 1} debe tener una cantidad entre 1 y {MaxCantidadPorLinea:N0}.");
                continue;
            }

            validas.Add((l.ProductoId, cantidad));
        }

        if (validas.Count == 0 && ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Agrega al menos un producto con cantidad.");
        }

        // Si el mismo producto quedó en dos líneas, se suman en una sola.
        var juntas = validas
            .GroupBy(v => v.ProductoId)
            .Select(g => (ProductoId: g.Key, Cantidad: g.Sum(v => v.Cantidad)))
            .ToList();

        foreach (var j in juntas.Where(j => j.Cantidad > MaxCantidadPorLinea))
        {
            ModelState.AddModelError(string.Empty,
                $"Un producto suma más de {MaxCantidadPorLinea:N0} unidades entre varias líneas.");
        }

        return juntas;
    }

    private async Task<int> ObtenerOCrearClienteAsync(NpgsqlConnection con, NpgsqlTransaction tx)
    {
        if (Mostrador)
        {
            // Sin datos del cliente: un único "Cliente de mostrador" compartido
            if (string.IsNullOrWhiteSpace(Cliente.Celular1))
            {
                Cliente.Celular1 = CelularMostrador;
                Cliente.NombreCliente = "Cliente de mostrador";
            }
            if (string.IsNullOrWhiteSpace(Cliente.Direccion)) Cliente.Direccion = "Venta en mostrador";
        }

        var celular = Cliente.Celular1.Trim();

        // Si el cliente ya existe, se usa su registro sin sobrescribir sus datos.
        var existente = await con.QueryFirstOrDefaultAsync<int?>(
            "SELECT id FROM clientes WHERE celular_1 = @c OR celular_2 = @c ORDER BY id LIMIT 1",
            new { c = celular }, tx);

        if (existente is not null)
        {
            return existente.Value;
        }

        return await con.ExecuteScalarAsync<int>(@"
            INSERT INTO clientes
                (nombre_ferreteria, nombre_cliente, direccion, ubicacion, horario, celular_1, celular_2)
            VALUES
                (@nf, @nc, @dir, @ubi, @hor, @c1, @c2)
            RETURNING id",
            new
            {
                nf = Nulo(Cliente.NombreFerreteria),
                nc = Cliente.NombreCliente.Trim(),
                dir = Cliente.Direccion.Trim(),
                ubi = Nulo(Cliente.Ubicacion),
                hor = Nulo(Cliente.Horario),
                c1 = celular,
                c2 = Nulo(Cliente.Celular2)
            }, tx);
    }

    private static string? Nulo(string? texto)
    {
        return string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
    }

    // ---------- Modelos ----------

    public class DatosCliente
    {
        public string Celular1 { get; set; } = "";
        public string? Celular2 { get; set; }
        public string? NombreFerreteria { get; set; }
        public string NombreCliente { get; set; } = "";
        public string Direccion { get; set; } = "";
        public string? Ubicacion { get; set; }
        public string? Horario { get; set; }
    }

    public class LineaPedido
    {
        public int ProductoId { get; set; }
        public int? Cantidad { get; set; }
    }

    public class ProductoOpcion
    {
        public int Id { get; set; }
        public string Codigo { get; set; } = "";
        public string? CodigoBarras { get; set; }
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioVenta { get; set; }   // por unidad
        public int PrecioPor { get; set; } = 1;
        public int Stock { get; set; }
        public string? Imagen { get; set; }
    }

    public class ProductoStock
    {
        public string Nombre { get; set; } = "";
        public decimal PrecioVenta { get; set; }
        public int Stock { get; set; }
    }

    public class VendedorInfo
    {
        public int Id { get; set; }
        public decimal DescuentoPct { get; set; }
    }

    public class ClienteEncontrado
    {
        public int Id { get; set; }
        public string? NombreFerreteria { get; set; }
        public string NombreCliente { get; set; } = "";
        public string Direccion { get; set; } = "";
        public string? Ubicacion { get; set; }
        public string? Horario { get; set; }
        public string? Celular2 { get; set; }
    }
}