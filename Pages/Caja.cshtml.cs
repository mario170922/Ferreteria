using System.Globalization;
using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// Caja del día (administrador y cajero):
// - Por cada conductor: lo que cobró en efectivo y en transferencia. El cajero cuenta
//   el efectivo que le entrega y cierra la caja de ese conductor; queda la diferencia.
// - Por cada vendedor: cuánto vendió ese día.
[Authorize(Roles = "admin,cajero")]
public class CajaModel : PageModel
{
    private readonly IConfiguration _config;

    public CajaModel(IConfiguration config) => _config = config;

    [BindProperty(SupportsGet = true)]
    public string? Fecha { get; set; }

    public DateTime Dia { get; set; }
    public List<CajaConductor> Conductores { get; set; } = new();
    public List<VentaVendedor> Vendedores { get; set; } = new();
    public List<VentaMostrador> Mostrador { get; set; } = new();
    public string? Mensaje { get; set; }
    public string? Error { get; set; }

    private static readonly CultureInfo Co = new("es-CO");

    public string DiaTexto => char.ToUpper(Dia.ToString("dddd d 'de' MMMM 'de' yyyy", Co)[0]) + Dia.ToString("dddd d 'de' MMMM 'de' yyyy", Co)[1..];

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"] as string;
        Error = TempData["Error"] as string;
        Dia = LeerFecha(Fecha);

        var negocio = User.NegocioActual();
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        Conductores = (await con.QueryAsync<CajaConductor>(@"
            SELECT u.id AS Id, u.nombre_usuario AS Nombre,
                   count(p.id) FILTER (WHERE p.estado = 'entregado')                                   AS Entregados,
                   count(p.id) FILTER (WHERE p.estado = 'no_entregado')                                AS NoEntregados,
                   COALESCE(SUM(p.pago_valor) FILTER (WHERE p.estado = 'entregado' AND p.pago_metodo = 'efectivo'), 0)      AS Efectivo,
                   COALESCE(SUM(p.pago_valor) FILTER (WHERE p.estado = 'entregado' AND p.pago_metodo = 'transferencia'), 0) AS Transferencias,
                   (SELECT count(*) FROM pedidos x WHERE x.conductor_id = u.id AND x.negocio = @negocio
                       AND x.estado IN ('pendiente', 'alistado', 'en_ruta'))                           AS PorEntregar,
                   cc.efectivo_recibido AS Recibido, cc.nota AS Nota, cc.creado_en AS CerradoEn,
                   (SELECT nombre_usuario FROM usuarios WHERE id = cc.cajero_id) AS CerradoPor
            FROM usuarios u
            LEFT JOIN pedidos p ON p.conductor_id = u.id AND p.negocio = @negocio AND p.entregado_en::date = @dia
            LEFT JOIN cierres_caja cc ON cc.conductor_id = u.id AND cc.negocio = @negocio AND cc.fecha = @dia
            WHERE u.rol = 'conductor' AND u.negocio = @negocio
            GROUP BY u.id, u.nombre_usuario, cc.efectivo_recibido, cc.nota, cc.creado_en, cc.cajero_id
            HAVING u.activo OR count(p.id) > 0 OR bool_or(cc.id IS NOT NULL)
            ORDER BY u.nombre_usuario", new { negocio, dia = Dia })).ToList();

        // Ventas de mostrador del día, por quien cobró
        Mostrador = (await con.QueryAsync<VentaMostrador>(@"
            SELECT u.nombre_usuario AS Nombre, count(*) AS Ventas,
                   COALESCE(SUM(p.pago_valor) FILTER (WHERE p.pago_metodo = 'efectivo'), 0)      AS Efectivo,
                   COALESCE(SUM(p.pago_valor) FILTER (WHERE p.pago_metodo = 'transferencia'), 0) AS Transferencias
            FROM pedidos p JOIN usuarios u ON u.id = p.vendedor_id
            WHERE p.negocio = @negocio AND p.tipo = 'mostrador' AND p.estado = 'entregado'
              AND p.entregado_en::date = @dia
            GROUP BY u.nombre_usuario
            ORDER BY u.nombre_usuario", new { negocio, dia = Dia })).ToList();

        Vendedores = (await con.QueryAsync<VentaVendedor>(@"
            SELECT u.nombre_usuario AS Nombre, count(*) AS Pedidos, SUM(p.total) AS Total
            FROM pedidos p JOIN usuarios u ON u.id = p.vendedor_id
            WHERE p.negocio = @negocio AND p.fecha::date = @dia
              AND p.estado NOT IN ('cancelado', 'no_entregado')
            GROUP BY u.nombre_usuario
            ORDER BY SUM(p.total) DESC", new { negocio, dia = Dia })).ToList();
    }

    // Cerrar la caja de un conductor: el cajero escribe el efectivo que recibió
    public async Task<IActionResult> OnPostCerrarAsync(int conductorId, decimal recibido, string? nota)
    {
        var dia = LeerFecha(Fecha);
        var negocio = User.NegocioActual();
        if (recibido < 0)
        {
            TempData["Error"] = "El efectivo recibido no puede ser negativo.";
            return RedirectToPage(new { fecha = dia.ToString("yyyy-MM-dd") });
        }

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        var totales = await con.QueryFirstOrDefaultAsync<(decimal Efectivo, decimal Transferencias)?>(@"
            SELECT COALESCE(SUM(p.pago_valor) FILTER (WHERE p.estado = 'entregado' AND p.pago_metodo = 'efectivo'), 0),
                   COALESCE(SUM(p.pago_valor) FILTER (WHERE p.estado = 'entregado' AND p.pago_metodo = 'transferencia'), 0)
            FROM usuarios u
            LEFT JOIN pedidos p ON p.conductor_id = u.id AND p.negocio = @negocio AND p.entregado_en::date = @dia
            WHERE u.id = @conductorId AND u.rol = 'conductor' AND u.negocio = @negocio
            GROUP BY u.id", new { conductorId, negocio, dia });

        if (totales is null)
        {
            TempData["Error"] = "Ese conductor no es de este negocio.";
            return RedirectToPage(new { fecha = dia.ToString("yyyy-MM-dd") });
        }

        var cajeroId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var c) ? c : (int?)null;
        try
        {
            await con.ExecuteAsync(@"
                INSERT INTO cierres_caja (negocio, fecha, conductor_id, efectivo_esperado, transferencias, efectivo_recibido, nota, cajero_id)
                VALUES (@negocio, @dia, @conductorId, @efectivo, @transferencias, @recibido, @nota, @cajeroId)",
                new
                {
                    negocio, dia, conductorId, efectivo = totales.Value.Efectivo, transferencias = totales.Value.Transferencias,
                    recibido, nota = string.IsNullOrWhiteSpace(nota) ? null : (nota.Trim().Length > 300 ? nota.Trim()[..300] : nota.Trim()), cajeroId
                });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            TempData["Error"] = "La caja de ese conductor ya estaba cerrada para ese día.";
            return RedirectToPage(new { fecha = dia.ToString("yyyy-MM-dd") });
        }

        var diferencia = recibido - totales.Value.Efectivo;
        TempData["Mensaje"] = diferencia == 0
            ? "Caja cerrada: el efectivo cuadra."
            : $"Caja cerrada con una diferencia de {Pedidos.ListaModel.Moneda(diferencia)} ({(diferencia > 0 ? "sobra" : "falta")}).";
        return RedirectToPage(new { fecha = dia.ToString("yyyy-MM-dd") });
    }

    private static DateTime LeerFecha(string? fecha) =>
        DateTime.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) && f.Date <= DateTime.Today
            ? f.Date : DateTime.Today;

    public class CajaConductor
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public long Entregados { get; set; }
        public long NoEntregados { get; set; }
        public decimal Efectivo { get; set; }
        public decimal Transferencias { get; set; }
        public long PorEntregar { get; set; }
        public decimal? Recibido { get; set; }
        public string? Nota { get; set; }
        public DateTime? CerradoEn { get; set; }
        public string? CerradoPor { get; set; }
        public bool Cerrada => Recibido is not null;
        public decimal Diferencia => (Recibido ?? 0) - Efectivo;
    }

    public class VentaMostrador
    {
        public string Nombre { get; set; } = "";
        public long Ventas { get; set; }
        public decimal Efectivo { get; set; }
        public decimal Transferencias { get; set; }
    }

    // Efectivo que debería haber en la caja: lo de mostrador más lo que entregaron los conductores
    public decimal EfectivoEnCaja => Mostrador.Sum(m => m.Efectivo) + Conductores.Sum(c => c.Recibido ?? 0);

    public class VentaVendedor
    {
        public string Nombre { get; set; } = "";
        public long Pedidos { get; set; }
        public decimal Total { get; set; }
    }
}
