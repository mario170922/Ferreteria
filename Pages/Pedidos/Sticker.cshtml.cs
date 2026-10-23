using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;
using QRCoder;

namespace Ferreteria.Pages.Pedidos;

[Authorize(Roles = "admin")]
public class StickerModel : PageModel
{
    private readonly IConfiguration _config;

    public StickerModel(IConfiguration config)
    {
        _config = config;
    }

    public Alistamiento Pedido { get; set; } = new();
    public string? Mensaje { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        var pedido = await Alistamiento.CargarAsync(con, id, Ferreteria.Services.Negocio.NegocioActual(User));
        if (pedido is null) return NotFound();

        Pedido = pedido;
        Mensaje = TempData["Mensaje"] as string;

        // El QR lleva al detalle del pedido, para que el conductor lo escanee.
        // Se genera aquí mismo, así que no necesita internet para imprimirse.
        var url = $"{Request.Scheme}://{Request.Host}/Pedidos/Detalle/{id}";
        using var generador = new QRCodeGenerator();
        using var datos = generador.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        QrSvg = new SvgQRCode(datos).GetGraphic(10, "#111111", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
        return Page();
    }

    public string QrSvg { get; set; } = "";
}
