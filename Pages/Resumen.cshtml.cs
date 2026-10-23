using System.Globalization;
using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// Resumen del negocio actual por día, semana, mes, semestre o año:
// ingresos (ventas), egresos (compras de mercancía), lo más y lo menos vendido.
//
// - Ingresos: total de los pedidos del período, sin contar cancelados ni no entregados.
// - Egresos: el total de las facturas de compra cargadas en Compras (por la fecha de la
//   factura), más las entradas manuales hechas en Movimientos (sin devoluciones de pedidos,
//   ajustes ni stock inicial) por su precio de compra actual.
[Authorize(Roles = "admin,cajero")]
public class ResumenModel : PageModel
{
    private readonly IConfiguration _config;

    public ResumenModel(IConfiguration config) => _config = config;

    public static readonly (string Clave, string Nombre)[] Periodos =
    {
        ("dia", "Día"), ("semana", "Semana"), ("mes", "Mes"), ("semestre", "Semestre"), ("anio", "Año")
    };

    [BindProperty(SupportsGet = true)]
    public string Periodo { get; set; } = "dia";

    // Cualquier fecha dentro del período que se quiere ver (yyyy-MM-dd)
    [BindProperty(SupportsGet = true)]
    public string? Fecha { get; set; }

    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }   // exclusivo
    public DateTime Anterior { get; set; }
    public DateTime Siguiente { get; set; }
    public bool EsActual { get; set; }
    public string Titulo { get; set; } = "";

    public Totales Datos { get; set; } = new();
    public Totales DatosAnterior { get; set; } = new();
    public List<Tramo> Tramos { get; set; } = new();
    public List<ProductoVendido> MasVendidos { get; set; } = new();
    public List<ProductoVendido> MenosVendidos { get; set; } = new();

    private static readonly CultureInfo Co = new("es-CO");

    public async Task OnGetAsync()
    {
        if (Periodos.All(p => p.Clave != Periodo)) Periodo = "dia";
        var dia = DateTime.TryParseExact(Fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            ? f.Date : DateTime.Today;
        if (dia > DateTime.Today) dia = DateTime.Today;

        (Desde, Hasta) = Rango(Periodo, dia);
        Anterior = Desde.AddDays(-1);
        Siguiente = Hasta;
        EsActual = DateTime.Today >= Desde && DateTime.Today < Hasta;
        Titulo = TituloDe(Periodo, Desde, Hasta);

        var negocio = User.NegocioActual();
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        Datos = await TotalesAsync(con, negocio, Desde, Hasta);
        var (dA, hA) = Rango(Periodo, Anterior);
        DatosAnterior = await TotalesAsync(con, negocio, dA, hA);

        // Barras por tramos: por día en semana y mes, por mes en semestre y año
        if (Periodo != "dia")
        {
            var porMes = Periodo is "semestre" or "anio";
            var paso = porMes ? "1 month" : "1 day";
            var corte = porMes ? "month" : "day";
            Tramos = (await con.QueryAsync<Tramo>($@"
                SELECT t::date AS Inicio,
                       COALESCE((SELECT SUM(p.total) FROM pedidos p
                                  WHERE p.negocio = @negocio
                                    AND p.estado NOT IN ('cancelado', 'no_entregado')
                                    AND date_trunc('{corte}', p.fecha) = t), 0) AS Ingresos
                FROM generate_series(@desde::timestamp, @hasta::timestamp - interval '1 second', interval '{paso}') t
                ORDER BY t", new { negocio, desde = Desde, hasta = Hasta })).ToList();
        }

        const string vendidos = @"
            SELECT pr.id AS Id, pr.nombre AS Nombre, pr.medida AS Medida, pr.stock AS Stock,
                   COALESCE(SUM(pi.cantidad) FILTER (WHERE p.id IS NOT NULL), 0) AS Unidades,
                   COALESCE(SUM(pi.cantidad * pi.precio_unitario) FILTER (WHERE p.id IS NOT NULL), 0) AS Valor
            FROM productos pr
            LEFT JOIN pedido_items pi ON pi.producto_id = pr.id
            LEFT JOIN pedidos p ON p.id = pi.pedido_id
                               AND p.estado NOT IN ('cancelado', 'no_entregado')
                               AND p.fecha >= @desde AND p.fecha < @hasta
            WHERE pr.negocio = @negocio
            GROUP BY pr.id, pr.nombre, pr.medida, pr.stock";

        var todos = (await con.QueryAsync<ProductoVendido>(vendidos, new { negocio, desde = Desde, hasta = Hasta })).ToList();
        MasVendidos = todos.Where(p => p.Unidades > 0).OrderByDescending(p => p.Unidades).ThenByDescending(p => p.Valor).Take(10).ToList();
        // Lo que menos salió, incluidos los que no se vendieron nada (pero sí tienen existencias)
        MenosVendidos = todos.Where(p => p.Unidades > 0 || p.Stock > 0)
            .OrderBy(p => p.Unidades).ThenByDescending(p => p.Stock).Take(10).ToList();
    }

    private static async Task<Totales> TotalesAsync(NpgsqlConnection con, string negocio, DateTime desde, DateTime hasta)
    {
        return await con.QueryFirstAsync<Totales>(@"
            SELECT
                (SELECT COALESCE(SUM(total), 0) FROM pedidos
                  WHERE negocio = @negocio AND estado NOT IN ('cancelado', 'no_entregado')
                    AND fecha >= @desde AND fecha < @hasta)                                   AS Ingresos,
                (SELECT count(*) FROM pedidos
                  WHERE negocio = @negocio AND estado NOT IN ('cancelado', 'no_entregado')
                    AND fecha >= @desde AND fecha < @hasta)                                   AS Pedidos,
                (SELECT COALESCE(SUM(pi.cantidad * pr.precio_compra), 0)
                   FROM pedido_items pi
                   JOIN pedidos p    ON p.id = pi.pedido_id
                   JOIN productos pr ON pr.id = pi.producto_id
                  WHERE p.negocio = @negocio AND p.estado NOT IN ('cancelado', 'no_entregado')
                    AND p.fecha >= @desde AND p.fecha < @hasta)                               AS CostoVendido,
                (SELECT COALESCE(SUM(total), 0) FROM compras
                  WHERE negocio = @negocio AND fecha >= @desde AND fecha < @hasta)
                + (SELECT COALESCE(SUM(m.cantidad * pr.precio_compra), 0)
                   FROM movimientos_inventario m
                   JOIN productos pr ON pr.id = m.producto_id
                  WHERE pr.negocio = @negocio AND m.cantidad > 0
                    AND m.motivo NOT ILIKE 'Devoluci%' AND m.motivo NOT ILIKE 'Ajuste%'
                    AND m.motivo NOT ILIKE 'Compra:%' AND m.motivo <> 'Stock inicial'
                    AND m.fecha >= @desde AND m.fecha < @hasta)                               AS Egresos",
            new { negocio, desde, hasta });
    }

    public static (DateTime Desde, DateTime Hasta) Rango(string periodo, DateTime dia) => periodo switch
    {
        "semana" => (dia.AddDays(-(((int)dia.DayOfWeek + 6) % 7)), dia.AddDays(-(((int)dia.DayOfWeek + 6) % 7) + 7)),
        "mes" => (new DateTime(dia.Year, dia.Month, 1), new DateTime(dia.Year, dia.Month, 1).AddMonths(1)),
        "semestre" => (new DateTime(dia.Year, dia.Month <= 6 ? 1 : 7, 1), new DateTime(dia.Year, dia.Month <= 6 ? 1 : 7, 1).AddMonths(6)),
        "anio" => (new DateTime(dia.Year, 1, 1), new DateTime(dia.Year + 1, 1, 1)),
        _ => (dia, dia.AddDays(1))
    };

    private static string TituloDe(string periodo, DateTime desde, DateTime hasta)
    {
        string Mayus(string s) => char.ToUpper(s[0]) + s[1..];
        return periodo switch
        {
            "semana" => $"Semana del {desde.ToString("d 'de' MMMM", Co)} al {hasta.AddDays(-1).ToString("d 'de' MMMM yyyy", Co)}",
            "mes" => Mayus(desde.ToString("MMMM 'de' yyyy", Co)),
            "semestre" => $"{(desde.Month == 1 ? "Primer" : "Segundo")} semestre de {desde.Year}",
            "anio" => $"Año {desde.Year}",
            _ => Mayus(desde.ToString("dddd d 'de' MMMM 'de' yyyy", Co))
        };
    }

    public string EtiquetaTramo(DateTime inicio) => Periodo is "semestre" or "anio"
        ? inicio.ToString("MMM", Co).TrimEnd('.')
        : Periodo == "semana" ? inicio.ToString("ddd", Co).TrimEnd('.') : inicio.Day.ToString();

    public string Enlace(string periodo, DateTime fecha) => $"/Resumen?periodo={periodo}&fecha={fecha:yyyy-MM-dd}";

    public class Totales
    {
        public decimal Ingresos { get; set; }
        public long Pedidos { get; set; }
        public decimal CostoVendido { get; set; }
        public decimal Egresos { get; set; }
        public decimal Ganancia => Ingresos - CostoVendido;
    }

    public class Tramo
    {
        public DateTime Inicio { get; set; }
        public decimal Ingresos { get; set; }
    }

    public class ProductoVendido
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string? Medida { get; set; }
        public int Stock { get; set; }
        public long Unidades { get; set; }
        public decimal Valor { get; set; }
    }
}
