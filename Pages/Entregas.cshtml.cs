using Dapper;
using Ferreteria.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages;

// "Mis entregas": la pantalla del conductor en el celular.
// Ve los pedidos que le asignaron, abre la dirección en Waze o Google Maps,
// marca cuando sale, y al llegar registra si entregó y cómo cobró.
[Authorize(Roles = "conductor")]
public class EntregasModel : PageModel
{
    private readonly IConfiguration _config;

    public EntregasModel(IConfiguration config) => _config = config;

    public List<Entrega> Pendientes { get; set; } = new();
    public List<Entrega> Terminadas { get; set; } = new();
    public string? Mensaje { get; set; }
    public string? Error { get; set; }

    private int MiId => int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    public async Task OnGetAsync()
    {
        Mensaje = TempData["Mensaje"] as string;
        Error = TempData["Error"] as string;

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        const string sql = @"
            SELECT p.id AS Id, p.numero AS Numero, p.estado AS Estado, p.total AS Total,
                   p.pago_metodo AS PagoMetodo, p.pago_valor AS PagoValor, p.entregado_en AS EntregadoEn,
                   c.nombre_cliente AS Cliente, c.nombre_ferreteria AS Ferreteria, c.direccion AS Direccion,
                   c.ubicacion AS Ubicacion, c.horario AS Horario, c.celular_1 AS Celular1, c.celular_2 AS Celular2,
                   (SELECT COALESCE(SUM(cantidad), 0) FROM pedido_items WHERE pedido_id = p.id) AS Unidades
            FROM pedidos p
            JOIN clientes c ON c.id = p.cliente_id
            WHERE p.conductor_id = @yo AND p.negocio = @negocio
              AND (p.estado IN ('pendiente', 'alistado', 'en_ruta')
                   OR (p.estado IN ('entregado', 'no_entregado') AND p.entregado_en::date = current_date))
            ORDER BY p.asignado_en, p.id";

        var todas = (await con.QueryAsync<Entrega>(sql, new { yo = MiId, negocio = User.NegocioActual() })).ToList();
        Pendientes = todas.Where(e => e.Estado is "pendiente" or "alistado" or "en_ruta").ToList();
        Terminadas = todas.Except(Pendientes).ToList();
    }

    // Sale a entregar: el pedido pasa a "En ruta"
    public Task<IActionResult> OnPostSalirAsync(int id) =>
        CambiarAsync(id, "en_ruta", null, "Pedido en ruta. Buen viaje.");

    // Entregado y cobrado
    public async Task<IActionResult> OnPostEntregarAsync(int id, string metodo, decimal valor, string? referencia)
    {
        if (metodo is not ("efectivo" or "transferencia"))
        {
            TempData["Error"] = "Elige cómo pagó el cliente: efectivo o transferencia.";
            return RedirectToPage();
        }
        if (valor <= 0)
        {
            TempData["Error"] = "Escribe cuánto cobró.";
            return RedirectToPage();
        }

        return await CambiarAsync(id, "entregado", (metodo, valor, referencia?.Trim()), "¡Pedido entregado!");
    }

    // No se pudo entregar: el stock vuelve al inventario
    public async Task<IActionResult> OnPostNoEntregarAsync(int id, string? nota)
    {
        var r = await CambiarAsync(id, "no_entregado", null, "Pedido marcado como no entregado. La mercancía vuelve al inventario.");
        if (!string.IsNullOrWhiteSpace(nota))
        {
            await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
            await con.ExecuteAsync("UPDATE pedidos SET nota_entrega = @nota WHERE id = @id AND conductor_id = @yo",
                new { nota = nota.Trim().Length > 300 ? nota.Trim()[..300] : nota.Trim(), id, yo = MiId });
        }
        return r;
    }

    private async Task<IActionResult> CambiarAsync(int id, string estado, (string Metodo, decimal Valor, string? Referencia)? pago, string ok)
    {
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        try
        {
            // Solo pedidos asignados a este conductor y todavía sin terminar
            var esMio = await con.ExecuteScalarAsync<bool>(@"
                SELECT EXISTS (SELECT 1 FROM pedidos WHERE id = @id AND conductor_id = @yo
                               AND estado IN ('pendiente', 'alistado', 'en_ruta'))", new { id, yo = MiId }, tx);
            if (!esMio)
                throw new InvalidOperationException("Ese pedido ya no está en tu lista.");

            await EstadoPedido.CambiarAsync(con, tx, id, User.NegocioActual(), estado);

            if (estado is "entregado" or "no_entregado")
            {
                await con.ExecuteAsync(@"
                    UPDATE pedidos SET entregado_en = now(),
                        pago_metodo = @metodo, pago_valor = @valor, pago_referencia = @referencia
                    WHERE id = @id",
                    new { id, metodo = pago?.Metodo, valor = pago?.Valor, referencia = pago?.Referencia }, tx);
            }

            await tx.CommitAsync();
            TempData["Mensaje"] = ok;
        }
        catch (InvalidOperationException ex)
        {
            await tx.RollbackAsync();
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    // ---------- Mapa ----------

    // Texto que se busca en el mapa: dirección, barrio/ubicación, ciudad y país
    public string Destino(Entrega e)
    {
        var partes = new List<string> { e.Direccion };
        if (!string.IsNullOrWhiteSpace(e.Ubicacion) && !EsEnlace(e.Ubicacion)) partes.Add(e.Ubicacion!);
        // La ciudad se puede cambiar en appsettings.json ("Empresa": { "Ciudad": ... }); si no está, Bogotá
        partes.Add(_config["Empresa:Ciudad"] is { Length: > 0 } ciudad ? ciudad : "Bogotá");
        partes.Add("Colombia");
        return string.Join(", ", partes);
    }

    public static bool EsEnlace(string? texto) => texto is not null && texto.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    public string EnlaceGoogle(Entrega e) => EsEnlace(e.Ubicacion)
        ? e.Ubicacion!
        : "https://www.google.com/maps/dir/?api=1&travelmode=driving&destination=" + Uri.EscapeDataString(Destino(e));

    public string EnlaceWaze(Entrega e) =>
        "https://waze.com/ul?navigate=yes&q=" + Uri.EscapeDataString(Destino(e));

    // Ruta con todas las paradas pendientes, en el orden de la lista (Google Maps acepta hasta 9 paradas intermedias)
    public string? EnlaceRuta()
    {
        var paradas = Pendientes.Take(10).Select(Destino).ToList();
        if (paradas.Count < 2) return null;
        var destino = paradas[^1];
        var intermedias = string.Join("|", paradas.Take(paradas.Count - 1));
        return "https://www.google.com/maps/dir/?api=1&travelmode=driving&destination=" + Uri.EscapeDataString(destino)
            + "&waypoints=" + Uri.EscapeDataString(intermedias);
    }

    public class Entrega
    {
        public int Id { get; set; }
        public string Numero { get; set; } = "";
        public string Estado { get; set; } = "";
        public decimal Total { get; set; }
        public string? PagoMetodo { get; set; }
        public decimal? PagoValor { get; set; }
        public DateTime? EntregadoEn { get; set; }
        public string Cliente { get; set; } = "";
        public string? Ferreteria { get; set; }
        public string Direccion { get; set; } = "";
        public string? Ubicacion { get; set; }
        public string? Horario { get; set; }
        public string Celular1 { get; set; } = "";
        public string? Celular2 { get; set; }
        public long Unidades { get; set; }
    }
}
