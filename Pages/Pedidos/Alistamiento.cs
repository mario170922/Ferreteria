using Dapper;
using Npgsql;

namespace Ferreteria.Pages.Pedidos;

// Datos que necesitan las páginas de alistar y de sticker.
public class Alistamiento
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string Estado { get; set; } = "";
    public string? Ferreteria { get; set; }
    public string Cliente { get; set; } = "";
    public string Direccion { get; set; } = "";
    public string? Ubicacion { get; set; }
    public string? Horario { get; set; }
    public string Celular1 { get; set; } = "";
    public string? Celular2 { get; set; }
    public List<Linea> Items { get; set; } = new();

    public int TotalUnidades => Items.Sum(i => i.Cantidad);

    // Solo encuentra el pedido si es del negocio indicado.
    public static async Task<Alistamiento?> CargarAsync(NpgsqlConnection con, int id, string negocio)
    {
        var pedido = await con.QueryFirstOrDefaultAsync<Alistamiento>(@"
            SELECT p.id                AS Id,
                   p.numero            AS Numero,
                   p.fecha             AS Fecha,
                   p.estado            AS Estado,
                   c.nombre_ferreteria AS Ferreteria,
                   c.nombre_cliente    AS Cliente,
                   c.direccion         AS Direccion,
                   c.ubicacion         AS Ubicacion,
                   c.horario           AS Horario,
                   c.celular_1         AS Celular1,
                   c.celular_2         AS Celular2
            FROM pedidos p
            JOIN clientes c ON c.id = p.cliente_id
            WHERE p.id = @id AND p.negocio = @negocio", new { id, negocio });

        if (pedido is null) return null;

        var items = await con.QueryAsync<Linea>(@"
            SELECT pr.codigo   AS Codigo,
                   pr.nombre   AS Nombre,
                   pr.medida   AS Medida,
                   pi.cantidad AS Cantidad
            FROM pedido_items pi
            JOIN productos pr ON pr.id = pi.producto_id
            WHERE pi.pedido_id = @id
            ORDER BY pr.nombre, pr.medida", new { id });

        pedido.Items = items.ToList();
        return pedido;
    }

    public class Linea
    {
        public string? Codigo { get; set; }
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int Cantidad { get; set; }
    }
}
