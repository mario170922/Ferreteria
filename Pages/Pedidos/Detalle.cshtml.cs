using Dapper;
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
        int vendedorId = 0;
        if (!EsAdmin)
        {
            vendedorId = await con.QueryFirstOrDefaultAsync<int>(
                "SELECT id FROM usuarios WHERE nombre_usuario = @nombre",
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
                   u.nombre_usuario     AS Vendedor
            FROM pedidos p
            JOIN clientes c ON c.id = p.cliente_id
            JOIN usuarios u ON u.id = p.vendedor_id
            WHERE p.id = @id
              AND (@esAdmin OR p.vendedor_id = @vid)";

        Pedido = await con.QueryFirstOrDefaultAsync<PedidoCabecera>(
            sqlCabecera,
            new { id, esAdmin = EsAdmin, vid = vendedorId });

        if (Pedido is null)
        {
            return NotFound();
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
            var actual = await con.QueryFirstOrDefaultAsync<string>(
                "SELECT estado FROM pedidos WHERE id = @id FOR UPDATE",
                new { id }, tx);

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

            bool eraDevuelto = EsDevuelto(actual);
            bool seraDevuelto = EsDevuelto(estado);

            // Pasa a "No entregado" o "Cancelado": el stock vuelve al inventario.
            if (!eraDevuelto && seraDevuelto)
            {
                await con.ExecuteAsync(@"
                    UPDATE productos p
                    SET stock = p.stock + pi.cantidad
                    FROM pedido_items pi
                    WHERE pi.pedido_id = @id AND pi.producto_id = p.id", new { id }, tx);
            }
            // Vuelve a un estado activo: se descuenta el stock otra vez, si hay existencias.
            else if (eraDevuelto && !seraDevuelto)
            {
                long faltantes = await con.ExecuteScalarAsync<long>(@"
                    SELECT COUNT(*)
                    FROM pedido_items pi
                    JOIN productos p ON p.id = pi.producto_id
                    WHERE pi.pedido_id = @id AND p.stock < pi.cantidad", new { id }, tx);

                if (faltantes > 0)
                {
                    throw new InvalidOperationException(
                        "No hay stock suficiente para volver a activar este pedido.");
                }

                await con.ExecuteAsync(@"
                    UPDATE productos p
                    SET stock = p.stock - pi.cantidad
                    FROM pedido_items pi
                    WHERE pi.pedido_id = @id AND pi.producto_id = p.id", new { id }, tx);
            }

            await con.ExecuteAsync(
                "UPDATE pedidos SET estado = @estado WHERE id = @id",
                new { estado, id }, tx);

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

    // ---------- Ayudas ----------

    private bool TieneRolAdmin()
    {
        return User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");
    }

    private static bool EsDevuelto(string estado)
    {
        return estado is "no_entregado" or "cancelado";
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