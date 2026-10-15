using System.Globalization;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Pedidos;

[Authorize]
public class ListaModel : PageModel
{
    private readonly IConfiguration _config;

    public ListaModel(IConfiguration config)
    {
        _config = config;
    }

    public List<PedidoFila> Pedidos { get; set; } = new();
    public bool EsAdmin { get; set; }

    public async Task OnGetAsync()
    {
        EsAdmin = User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");

        var connStr = _config.GetConnectionString("Ferreteria");
        await using var con = new NpgsqlConnection(connStr);
        await con.OpenAsync();

        int? vendedorId = null;
        if (!EsAdmin)
        {
            vendedorId = await con.QueryFirstOrDefaultAsync<int?>(
                "SELECT id FROM usuarios WHERE nombre_usuario = @nombre",
                new { nombre = User.Identity?.Name ?? "" });

            if (vendedorId is null)
            {
                return;
            }
        }

        string filtroVendedor = EsAdmin ? "" : "AND p.vendedor_id = @vid";

        string sql = $@"
            SELECT p.id                AS Id,
                   p.numero            AS Numero,
                   p.fecha             AS Fecha,
                   c.nombre_ferreteria AS Ferreteria,
                   c.nombre_cliente    AS Cliente,
                   u.nombre_usuario    AS Vendedor,
                   p.total             AS Total,
                   p.estado            AS Estado
            FROM pedidos p
            JOIN clientes c ON c.id = p.cliente_id
            JOIN usuarios u ON u.id = p.vendedor_id
            WHERE p.fecha >= now() - interval '3 months'
            {filtroVendedor}
            ORDER BY p.fecha DESC, p.id DESC";

        var parametros = new { vid = vendedorId ?? 0 };
        var lista = await con.QueryAsync<PedidoFila>(sql, parametros);
        Pedidos = lista.ToList();
    }

    // ---------- Ayudas para las vistas ----------

    public static string FormatearFecha(DateTime fecha)
    {
        return fecha.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Moneda(decimal valor)
    {
        return valor.ToString("C0", new CultureInfo("es-CO"));
    }

    public static string EtiquetaEstado(string estado)
    {
        return estado switch
        {
            "pendiente"    => "Pendiente",
            "alistado"     => "Alistado",
            "en_ruta"      => "En ruta",
            "entregado"    => "Entregado",
            "no_entregado" => "No entregado",
            "cancelado"    => "Cancelado",
            _              => estado
        };
    }

    public static string ClaseEstado(string estado)
    {
        return estado switch
        {
            "pendiente"    => "estado-pendiente",
            "alistado"     => "estado-alistado",
            "en_ruta"      => "estado-en_ruta",
            "entregado"    => "estado-entregado",
            "no_entregado" => "estado-no_entregado",
            "cancelado"    => "estado-cancelado",
            _              => "estado-pendiente"
        };
    }

    // ---------- Fila de la tabla ----------

    public class PedidoFila
    {
        public int Id { get; set; }
        public string Numero { get; set; } = "";
        public DateTime Fecha { get; set; }
        public string? Ferreteria { get; set; }
        public string Cliente { get; set; } = "";
        public string Vendedor { get; set; } = "";
        public decimal Total { get; set; }
        public string Estado { get; set; } = "";
    }
}