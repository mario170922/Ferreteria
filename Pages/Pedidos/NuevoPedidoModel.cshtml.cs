using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Pedidos;

[Authorize]
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

    [BindProperty]
    public DatosCliente Cliente { get; set; } = new();

    [BindProperty]
    public List<LineaPedido> Lineas { get; set; } = new();

    // ---------- Carga inicial de la pantalla ----------

    public async Task OnGetAsync()
    {
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
            SELECT nombre_ferreteria AS NombreFerreteria,
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

        return new JsonResult(new
        {
            encontrado = true,
            nombreFerreteria = c.NombreFerreteria ?? "",
            nombreCliente = c.NombreCliente,
            direccion = c.Direccion,
            ubicacion = c.Ubicacion ?? "",
            horario = c.Horario ?? "",
            celular2 = c.Celular2 ?? ""
        });
    }

    // ---------- Guardar pedido ----------

    public async Task<IActionResult> OnPostAsync()
    {
        await using var con = CrearConexion();
        await con.OpenAsync();

        ValidarCliente();
        var lineas = ValidarLineas();

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
                WHERE nombre_usuario = @n AND activo = true",
                new { n = User.Identity?.Name ?? "" }, tx);

            if (vendedor is null)
            {
                throw new InvalidOperationException("Tu usuario no está activo. Consulta con el administrador.");
            }

            if (DescuentoElegido < 0 || DescuentoElegido > vendedor.DescuentoPct)
            {
                throw new InvalidOperationException(
                    $"El descuento elegido supera tu máximo permitido ({vendedor.DescuentoPct:0.##}%).");
            }

            int idCliente = await ObtenerOCrearClienteAsync(con, tx);

            decimal subtotal = 0m;
            var detalles = new List<(int ProductoId, int Cantidad, decimal Precio)>();

            foreach (var (productoId, cantidad) in lineas)
            {
                var prod = await con.QueryFirstOrDefaultAsync<ProductoStock>(@"
                    SELECT nombre        AS Nombre,
                           precio_venta  AS PrecioVenta,
                           stock         AS Stock
                    FROM productos
                    WHERE id = @id
                    FOR UPDATE", new { id = productoId }, tx);

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
                INSERT INTO pedidos (cliente_id, vendedor_id, descuento_pct, total)
                VALUES (@cliente, @vendedor, @pct, @total)
                RETURNING id",
                new
                {
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

            await tx.CommitAsync();
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
            "SELECT descuento_pct FROM usuarios WHERE nombre_usuario = @n",
            new { n = NombreVendedor }) ?? 0m;

        var productos = await con.QueryAsync<ProductoOpcion>(@"
            SELECT id           AS Id,
                   codigo       AS Codigo,
                   nombre       AS Nombre,
                   medida       AS Medida,
                   precio_venta AS PrecioVenta,
                   stock        AS Stock
            FROM productos
            WHERE stock > 0
            ORDER BY codigo");

        Productos = productos.ToList();
    }

    private void ValidarCliente()
    {
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

        return validas;
    }

    private async Task<int> ObtenerOCrearClienteAsync(NpgsqlConnection con, NpgsqlTransaction tx)
    {
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
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public decimal PrecioVenta { get; set; }
        public int Stock { get; set; }
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
        public string? NombreFerreteria { get; set; }
        public string NombreCliente { get; set; } = "";
        public string Direccion { get; set; } = "";
        public string? Ubicacion { get; set; }
        public string? Horario { get; set; }
        public string? Celular2 { get; set; }
    }
}