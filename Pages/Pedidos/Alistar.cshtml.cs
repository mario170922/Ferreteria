using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Pedidos;

// Por ahora solo el administrador alista pedidos.
[Authorize(Roles = "admin")]
public class AlistarModel : PageModel
{
    private readonly IConfiguration _config;

    public AlistarModel(IConfiguration config)
    {
        _config = config;
    }

    public Alistamiento Pedido { get; set; } = new();
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        var pedido = await Alistamiento.CargarAsync(con, id);
        if (pedido is null) return NotFound();

        Pedido = pedido;
        Error = TempData["Error"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        // Solo un pedido pendiente puede pasar a alistado.
        int filas = await con.ExecuteAsync(
            "UPDATE pedidos SET estado = 'alistado' WHERE id = @id AND estado = 'pendiente'",
            new { id });

        if (filas == 0)
        {
            TempData["Error"] = "Este pedido ya no está pendiente, por eso no se marcó como alistado.";
            return RedirectToPage("/Pedidos/Alistar", new { id });
        }

        TempData["Mensaje"] = "Pedido alistado. Ya puedes imprimir el sticker.";
        return RedirectToPage("/Pedidos/Sticker", new { id });
    }
}
