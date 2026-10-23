using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;
using System.Security.Claims;

namespace Ferreteria.Pages.Pedidos;

[Authorize]
public class DetalleModel : PageModel
{
    private readonly IConfiguration _config;

    public DetalleModel(IConfiguration config)
    {
        _config = config;
    }

    public PedidoCabecera? Pedido { get; set; }
    public List<ItemDetalle> Items { get; set; } = new();
    public bool EsAdmin { get; set; }
    public List<(int Id, string Nombre)> Conductores { get; set; } = new();
    public string? Mensaje { get; set; }
    public string? Error { get; set; }

    public static readonly List<OpcionEstado> OpcionesEstado = new()
    {
        new("pendiente", "Pendiente"),
        new("alistado", "Alistado"),
        new("en_ruta", "En ruta"),
        new("entregado", "Entregado"),
        new("no_entregado", "No entregado"),
        new("cancelado", "Cancelado")
    };

    public decimal Subtotal => Items.Sum(i => i.Subtotal);
    public decimal MontoDescuento => Subtotal * (Pedido?.DescuentoPct ?? 0) / 100m;

    // ---------- Ver el pedido ----------

    public async Task<IActionResult> OnGetAsync(int id)
    {
        EsAdmin = TieneRolAdmin();
        Mensaje = TempData["Mensaje"] as string;
        Error = TempData["Error"] as string;

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await con.OpenAsync();

        // Para vendedores, buscamos su id. Si no existe, no mostramos nada.
        // Administrador, cajero y conductor ven cualquier pedido del negocio.
        var veTodos = User.VeTodosLosPedidos();
        int vendedorId = 0;
        if (!veTodos)
        {
            vendedorId = await con.QueryFirstOrDefaultAsync<int>(
                "SELECT id FROM usuarios WHERE lower(nombre_usuario) = lower(@nombre)",
                new { nombre = User.Identity?.Name ?? "" });

            if (vendedorId == 0)
            {
                return NotFound();
            }
        }

        // La condición de vendedor va dentro de la consulta: un vendedor
        // nunca puede traer un pedido que no sea suyo.
        const string sqlCabecera = @"
            SELECT p.id                 AS Id,
                   p.numero             AS Numero,
                   p.fecha              AS Fecha,
                   p.estado             AS Estado,
                   p.descuento_pct      AS DescuentoPct,
                   p.total              AS Total,
                   c.nombre_ferreteria  AS Ferreteria,
                   c.nombre_cliente     AS Cliente,
                   c.direccion          AS Direccion,
                   c.ubicacion          AS Ubicacion,
                   c.horario            AS Horario,
                   c.celular_1          AS Celular1,
                   c.celular_2          AS Celular2,
                   u.nombre_usuario     AS Vendedor,
                   p.conductor_id       AS ConductorId,
                   (SELECT nombre_usuario FROM usuarios WHERE id = p.conductor_id) AS Conductor,
                   p.pago_metodo        AS PagoMetodo,
                   p.pago_valor         AS PagoValor
            FROM pedidos p
            JOIN clientes c ON c.id = p.cliente_id
            JOIN usuarios u ON u.id = p.vendedor_id
            WHERE p.id = @id
              AND p.negocio = @negocio
              AND (@veTodos OR p.vendedor_id = @vid OR p.conductor_id = @vid)";

        Pedido = await con.QueryFirstOrDefaultAsync<PedidoCabecera>(
            sqlCabecera,
            new { id, veTodos, vid = vendedorId, negocio = User.NegocioActual() });

        if (Pedido is null)
        {
            // ¿Existe, pero es del otro negocio? Pasa cuando se abre un enlace o un QR
            // estando en el otro negocio.
            var otroNegocio = await con.QueryFirstOrDefaultAsync<string?>(
                "SELECT negocio FROM pedidos WHERE id = @id", new { id });

            if (otroNegocio is not null && otroNegocio != User.NegocioActual())
            {
                // El administrador global entra a los dos: se le cambia de negocio y se le muestra
                if (User.EsAdminGlobal())
                {
                    var usuario = await con.QuerySingleOrDefaultAsync<Ferreteria.Models.Usuario>(@"
                        SELECT id, nombre_usuario AS NombreUsuario, rol, negocio, activo AS Activo
                        FROM usuarios WHERE lower(nombre_usuario) = lower(@n)", new { n = User.Identity?.Name ?? "" });
                    if (usuario is { Activo: true, Rol: Negocio.AdminGlobal })
                    {
                        await Ferreteria.Pages.LoginModel.IniciarSesionAsync(HttpContext, usuario, otroNegocio);
                        TempData["Mensaje"] = $"Este pedido es de {Negocio.Nombre(otroNegocio)}: te pasé a {Negocio.Nombre(otroNegocio)}.";
                        return RedirectToPage("/Pedidos/Detalle", new { id });
                    }
                }

                TempData["Error"] = $"Ese pedido es de {Negocio.Nombre(otroNegocio)}. Entra por {Negocio.Nombre(otroNegocio)} para verlo.";
                return RedirectToPage("/Pedidos/Lista");
            }

            TempData["Error"] = "Ese pedido no existe o no lo tienes asignado.";
            return RedirectToPage("/Pedidos/Lista");
        }

        const string sqlItems = @"
            SELECT pr.codigo          AS Codigo,
                   pr.nombre          AS Nombre,
                   pr.medida          AS Medida,
                   pi.cantidad        AS Cantidad,
                   pi.precio_unitario AS PrecioUnitario
            FROM pedido_items pi
            JOIN productos pr ON pr.id = pi.producto_id
            WHERE pi.pedido_id = @id
            ORDER BY pi.id";

        var items = await con.QueryAsync<ItemDetalle>(sqlItems, new { id });
        Items = items.ToList();

        if (User.ManejaCaja())
        {
            Conductores = (await con.QueryAsync<(int Id, string Nombre)>(@"
                SELECT id, nombre_usuario FROM usuarios
                WHERE rol = 'conductor' AND activo AND negocio = @negocio
                ORDER BY nombre_usuario", new { negocio = User.NegocioActual() })).ToList();
        }

        return Page();
    }

    // ---------- Cambiar estado (solo admin) ----------

    public async Task<IActionResult> OnPostEstadoAsync(int id, string estado)
    {
        if (!TieneRolAdmin())
        {
            return Forbid();
        }

        if (!OpcionesEstado.Any(o => o.Clave == estado))
        {
            TempData["Error"] = "El estado elegido no es válido.";
            return RedirectToPage("/Pedidos/Detalle", new { id });
        }

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        try
        {
            var actual = await EstadoPedido.CambiarAsync(con, tx, id, User.NegocioActual(), estado);

            if (actual is null)
            {
                await tx.RollbackAsync();
                return NotFound();
            }

            if (actual == estado)
            {
                await tx.RollbackAsync();
                TempData["Mensaje"] = "El pedido ya está en ese estado.";
                return RedirectToPage("/Pedidos/Detalle", new { id });
            }

            await tx.CommitAsync();
            TempData["Mensaje"] = $"Estado cambiado a {EtiquetaDe(estado)}.";
        }
        catch (InvalidOperationException ex)
        {
            await tx.RollbackAsync();
            TempData["Error"] = ex.Message;
        }
        catch (Exception)
        {
            await tx.RollbackAsync();
            TempData["Error"] = "No se pudo cambiar el estado. Intenta de nuevo.";
        }

        return RedirectToPage("/Pedidos/Detalle", new { id });
    }

    // ---------- Asignar conductor (administrador y cajero) ----------

    public async Task<IActionResult> OnPostAsignarAsync(int id, int? conductorId)
    {
        if (!User.ManejaCaja()) return Forbid();

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var negocio = User.NegocioActual();

        if (conductorId is null or 0)
        {
            await con.ExecuteAsync(@"
                UPDATE pedidos SET conductor_id = NULL, asignado_en = NULL
                WHERE id = @id AND negocio = @negocio AND estado IN ('pendiente', 'alistado', 'en_ruta')",
                new { id, negocio });
            TempData["Mensaje"] = "Se quitó el conductor del pedido.";
            return RedirectToPage("/Pedidos/Detalle", new { id });
        }

        // Solo conductores activos del mismo negocio, y solo pedidos que no se han terminado
        int filas = await con.ExecuteAsync(@"
            UPDATE pedidos SET conductor_id = @conductorId, asignado_en = now()
            WHERE id = @id AND negocio = @negocio AND estado IN ('pendiente', 'alistado', 'en_ruta')
              AND EXISTS (SELECT 1 FROM usuarios WHERE id = @conductorId AND rol = 'conductor'
                                                  AND activo AND negocio = @negocio)",
            new { id, conductorId, negocio });

        if (filas == 0)
            TempData["Error"] = "No se pudo asignar: el pedido ya terminó o el conductor no es de este negocio.";
        else
            TempData["Mensaje"] = "Conductor asignado. Ya puede ver el pedido en su celular.";
        return RedirectToPage("/Pedidos/Detalle", new { id });
    }

    // ---------- Ayudas ----------

    private bool TieneRolAdmin()
    {
        return User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");
    }

    private static string EtiquetaDe(string clave)
    {
        return OpcionesEstado.First(o => o.Clave == clave).Etiqueta;
    }

    // ---------- Modelos ----------

    public record OpcionEstado(string Clave, string Etiqueta);

    public class PedidoCabecera
    {
        public int Id { get; set; }
        public string Numero { get; set; } = "";
        public DateTime Fecha { get; set; }
        public string Estado { get; set; } = "";
        public decimal DescuentoPct { get; set; }
        public decimal Total { get; set; }
        public string? Ferreteria { get; set; }
        public string Cliente { get; set; } = "";
        public string Direccion { get; set; } = "";
        public string? Ubicacion { get; set; }
        public string? Horario { get; set; }
        public string Celular1 { get; set; } = "";
        public string? Celular2 { get; set; }
        public string Vendedor { get; set; } = "";
        public int? ConductorId { get; set; }
        public string? Conductor { get; set; }
        public string? PagoMetodo { get; set; }
        public decimal? PagoValor { get; set; }
    }

    public class ItemDetalle
    {
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int Cantidad { get; set; }
        public decimal PrecioUnitario { get; set; }
        public decimal Subtotal => Cantidad * PrecioUnitario;
    }
}