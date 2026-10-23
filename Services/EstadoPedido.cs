using Dapper;
using Npgsql;

namespace Ferreteria.Services;

// Cambio de estado de un pedido, con su efecto en el inventario.
// Lo usan el detalle del pedido (administrador) y la pantalla de entregas (conductor).
// - Pasa a "No entregado" o "Cancelado": el stock vuelve al inventario.
// - Vuelve a un estado activo desde uno de esos: se descuenta otra vez, si hay existencias.
public static class EstadoPedido
{
    public static readonly (string Clave, string Etiqueta)[] Estados =
    {
        ("pendiente", "Pendiente"), ("alistado", "Alistado"), ("en_ruta", "En ruta"),
        ("entregado", "Entregado"), ("no_entregado", "No entregado"), ("cancelado", "Cancelado")
    };

    public static string Etiqueta(string clave) => Estados.FirstOrDefault(e => e.Clave == clave).Etiqueta ?? clave;

    private static bool EsDevuelto(string estado) => estado is "no_entregado" or "cancelado";

    // Devuelve el estado que tenía, o null si el pedido no existe en ese negocio.
    // Lanza InvalidOperationException con un mensaje para mostrar si no se puede.
    public static async Task<string?> CambiarAsync(NpgsqlConnection con, NpgsqlTransaction tx, int id, string negocio, string estado)
    {
        if (Estados.All(e => e.Clave != estado))
            throw new InvalidOperationException("El estado elegido no es válido.");

        var actual = await con.QueryFirstOrDefaultAsync<string>(
            "SELECT estado FROM pedidos WHERE id = @id AND negocio = @negocio FOR UPDATE",
            new { id, negocio }, tx);

        if (actual is null || actual == estado) return actual;

        if (!EsDevuelto(actual) && EsDevuelto(estado))
        {
            await con.ExecuteAsync(@"
                UPDATE productos p
                SET stock = p.stock + pi.cantidad
                FROM pedido_items pi
                WHERE pi.pedido_id = @id AND pi.producto_id = p.id", new { id }, tx);

            await RegistrarMovimientosAsync(con, tx, id, 1, $"Devolución: pedido %s pasó a {Etiqueta(estado)}");
        }
        else if (EsDevuelto(actual) && !EsDevuelto(estado))
        {
            long faltantes = await con.ExecuteScalarAsync<long>(@"
                SELECT COUNT(*)
                FROM pedido_items pi
                JOIN productos p ON p.id = pi.producto_id
                WHERE pi.pedido_id = @id AND p.stock < pi.cantidad", new { id }, tx);

            if (faltantes > 0)
                throw new InvalidOperationException("No hay stock suficiente para volver a activar este pedido.");

            await con.ExecuteAsync(@"
                UPDATE productos p
                SET stock = p.stock - pi.cantidad
                FROM pedido_items pi
                WHERE pi.pedido_id = @id AND pi.producto_id = p.id", new { id }, tx);

            await RegistrarMovimientosAsync(con, tx, id, -1, $"Salida: pedido %s se reactivó como {Etiqueta(estado)}");
        }

        await con.ExecuteAsync("UPDATE pedidos SET estado = @estado WHERE id = @id", new { estado, id }, tx);
        return actual;
    }

    // Anota en movimientos_inventario cada producto del pedido.
    // signo = 1 es entrada (vuelve al inventario), signo = -1 es salida.
    // En el motivo, %s se reemplaza por el número del pedido.
    private static Task RegistrarMovimientosAsync(NpgsqlConnection con, NpgsqlTransaction tx, int id, int signo, string motivo) =>
        con.ExecuteAsync(@"
            INSERT INTO movimientos_inventario (producto_id, cantidad, motivo)
            SELECT pi.producto_id, @signo * pi.cantidad, format(@motivo, p.numero)
            FROM pedido_items pi
            JOIN pedidos p ON p.id = pi.pedido_id
            WHERE pi.pedido_id = @id",
            new { id, signo, motivo }, tx);
}
