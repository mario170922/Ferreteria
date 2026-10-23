using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Clientes;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IConfiguration _config;

    public IndexModel(IConfiguration config)
    {
        _config = config;
    }

    public List<ClienteFila> Lista { get; set; } = new();
    public string? Mensaje { get; set; }

    // Texto del buscador: nombre, ferretería o celular
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"]?.ToString();

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        var texto = Q?.Trim() ?? "";

        const string sql = @"
            SELECT c.id                AS Id,
                   c.nombre_ferreteria AS NombreFerreteria,
                   c.nombre_cliente    AS NombreCliente,
                   c.direccion         AS Direccion,
                   c.celular_1         AS Celular1,
                   c.celular_2         AS Celular2,
                   (SELECT count(*) FROM pedidos p WHERE p.cliente_id = c.id AND p.negocio = @negocio) AS Pedidos
            FROM clientes c
            WHERE @texto = ''
               OR c.nombre_cliente ILIKE '%' || @texto || '%'
               OR c.nombre_ferreteria ILIKE '%' || @texto || '%'
               OR c.celular_1 LIKE '%' || @texto || '%'
               OR c.celular_2 LIKE '%' || @texto || '%'
            ORDER BY c.nombre_cliente
            LIMIT 200";

        Lista = (await con.QueryAsync<ClienteFila>(sql, new { texto, negocio = User.NegocioActual() })).AsList();
    }

    public class ClienteFila
    {
        public int Id { get; set; }
        public string? NombreFerreteria { get; set; }
        public string NombreCliente { get; set; } = "";
        public string Direccion { get; set; } = "";
        public string Celular1 { get; set; } = "";
        public string? Celular2 { get; set; }
        public long Pedidos { get; set; }
    }
}
